CREATE TABLE IF NOT EXISTS users (
    id SERIAL PRIMARY KEY,
    username VARCHAR(32) NOT NULL UNIQUE,
    display_name VARCHAR(64) NOT NULL,
    email_lookup BYTEA UNIQUE,
    ciphertext BYTEA,
    tag BYTEA,
    nonce BYTEA,
    2fa_ciphertext BYTEA,
    2fa_tag BYTEA,
    2fa_nonce BYTEA,
    password_hash TEXT NOT NULL,
    about_me VARCHAR(250),
    pronouns VARCHAR(30),
    server_tag_id TEXT REFERENCES server_tag(id) ON DELETE SET NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    is_verified BOOLEAN NOT NULL DEFAULT FALSE,
    2fa_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    premium_expires_at TIMESTAMPTZ,
    dob TEXT NOT NULL,
    is_banned SMALLINT NOT NULL DEFAULT 0, -- 0 = Fine 1 = Banned 2 = Account Deleted
    profile_status SMALLINT NOT NULL DEFAULT 0 -- 0 = Idle 1 = dnd 2 = invisible 3 = online
);

CREATE TABLE IF NOT EXISTS user_message_reports (
    id SERIAL PRIMARY KEY,
    picture_path TEXT NOT NULL,
    reported_message VARCHAR(250) NOT NULL,
    user_id_reporter INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    user_id_reported INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    sent_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS blocked_users (
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    blocked_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS personal_profile_note (
    id UUID PRIMARY KEY NOT NULL,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    personal_note VARCHAR(250) NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS user_sessions (
    id SERIAL PRIMARY KEY,
    user_location TEXT NOT NULL,
    user_browser TEXT NOT NULL,
    user_os TEXT NOT NULL,
    user_id INTEGER NOT NULL,
    session_token VARCHAR(255) NOT NULL UNIQUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    active_at TIMESTAMPTZ DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS webhook_uploads (
    id UUID PRIMARY KEY NOT NULL REFERENCES server_channels_webhooks(id) ON DELETE CASCADE,
    file_name VARCHAR(255) NOT NULL,
    file_size BIGINT NOT NULL,
    mime_type VARCHAR(100),
    storage_path TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS avatar_uploads (
    id UUID PRIMARY KEY NOT NULL,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    file_name VARCHAR(255) NOT NULL,
    file_size BIGINT NOT NULL,
    mime_type VARCHAR(100),
    storage_path TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS role_icon_uploads (
    id UUID PRIMARY KEY NOT NULL,
    role_id INT NOT NULL REFERENCES server_roles(id) ON DELETE CASCADE,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    file_name VARCHAR(255) NOT NULL,
    file_size BIGINT NOT NULL,
    mime_type VARCHAR(100),
    storage_path TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS dm_conversations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    is_group BOOLEAN NOT NULL DEFAULT FALSE,
    name VARCHAR(100),
    owner_id INTEGER,
    dm_pair_key TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_dm_pair
ON dm_conversations (dm_pair_key)
WHERE is_group = FALSE
  AND dm_pair_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS dm_conversation_members (
    conversation_id UUID NOT NULL REFERENCES dm_conversations(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL,
    closed BOOLEAN NOT NULL DEFAULT TRUE,
    PRIMARY KEY (conversation_id, user_id)
);

CREATE INDEX IF NOT EXISTS idx_dm_members_user
ON dm_conversation_members (user_id, conversation_id);

CREATE TABLE IF NOT EXISTS dm_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id UUID NOT NULL REFERENCES dm_conversations(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL,
    message_content TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    edited BOOLEAN DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS server_message_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    message_id UUID NOT NULL REFERENCES server_messages(id) ON DELETE CASCADE,
    url TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS servers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_owner_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    server_name VARCHAR(100) NOT NULL,
    boosts_spent SMALLINT,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS server_boosts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users(id),
    started_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS server_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    url TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS server_members (
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    nickname VARCHAR(32),
    joined_at TIMESTAMP NOT NULL DEFAULT NOW(),
    PRIMARY KEY (server_id, user_id)
);

CREATE TABLE IF NOT EXISTS server_roles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    name VARCHAR(32) NOT NULL,
    color INTEGER,
    position INT NOT NULL DEFAULT 0,
    permissions BIGINT NOT NULL DEFAULT 0,
    separated BOOLEAN NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS server_channels (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    name VARCHAR(100) NOT NULL,
    type VARCHAR(20) NOT NULL, -- 'text', 'voice', 'category', 'forum'
    position INT NOT NULL DEFAULT 0,
    rules_channel BOOLEAN NOT NULL DEFAULT FALSE,
    channel_topic VARCHAR(100) DEFAULT '',
    channel_slowmode TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS server_forum_data (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    channel_id UUID NOT NULL REFERENCES server_channels(id) ON DELETE CASCADE,
    forum_title TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS channel_slowmode (
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    channel_id UUID NOT NULL REFERENCES server_channels(id) ON DELETE CASCADE,
    last_message_time TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS server_channels_webhooks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    creator_id INTEGER NOT NULL,
    channel_id UUID NOT NULL REFERENCES server_channels(id) ON DELETE CASCADE,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS server_settings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    systems_channel UUID REFERENCES server_channels(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS server_automod (
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    block_custom_words BOOLEAN NOT NULL DEFAULT FALSE,
    custom_words_list TEXT DEFAULT '',
    custom_phrases_words_allowed TEXT DEFAULT '',
    automod_word_violation_response SMALLINT NOT NULL DEFAULT 0, -- 0 = Block Message 1 = Send Alert 2 = Timeout Member
    automod_custom_words_rule_name VARCHAR(200) DEFAULT 'Block Custom Words',
    automod_channels_role_ids_bypass JSONB NOT NULL DEFAULT '{}'
);

CREATE TABLE IF NOT EXISTS server_bans (
    id BIGSERIAL PRIMARY KEY,
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users(id),
    moderator_id INTEGER NOT NULL REFERENCES users(id),
    reason TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMP NULL,
    UNIQUE (server_id, user_id)
);

CREATE TABLE IF NOT EXISTS server_mutes (
    id BIGSERIAL PRIMARY KEY,
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users(id),
    moderator_id INTEGER NOT NULL REFERENCES users(id),
    reason TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMP NOT NULL,
    UNIQUE (server_id, user_id)
);

CREATE TABLE IF NOT EXISTS channel_reads (
    user_id INTEGER NOT NULL REFERENCES users(id),
    channel_id UUID NOT NULL,
    last_read_message_id UUID NULL,
    last_read_at TIMESTAMP NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, channel_id),
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (channel_id) REFERENCES channels(id) ON DELETE CASCADE,
    FOREIGN KEY (last_read_message_id) REFERENCES server_messages(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS private_message_reads (
    user_id INTEGER NOT NULL REFERENCES users(id),
    conversation_id UUID NOT NULL,
    last_read_message_id UUID NULL,
    last_read_at TIMESTAMP NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, conversation_id),
    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
    FOREIGN KEY (conversation_id) REFERENCES private_conversations(id) ON DELETE CASCADE,
    FOREIGN KEY (last_read_message_id) REFERENCES dm_messages(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS server_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    channel_id UUID NOT NULL REFERENCES server_channels(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL,
    message_content TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    edited BOOLEAN DEFAULT FALSE,
    picture_path TEXT
);

CREATE TABLE IF NOT EXISTS reaction_uploads (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_name VARCHAR(255) NOT NULL,
    file_size BIGINT NOT NULL,
    mime_type VARCHAR(100),
    storage_path TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS private_message_reactions (
    message_id UUID NOT NULL REFERENCES dm_messages(id) ON DELETE CASCADE,
    reaction_id UUID NOT NULL REFERENCES reaction_uploads(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (message_id, reaction_id, user_id)
);

CREATE TABLE IF NOT EXISTS server_message_reactions (
    message_id UUID NOT NULL REFERENCES server_messages(id) ON DELETE CASCADE,
    reaction_id UUID NOT NULL REFERENCES reaction_uploads(id) ON DELETE CASCADE,
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (message_id, reaction_id, user_id)
);

CREATE TABLE IF NOT EXISTS server_message_mentions (
    message_id UUID NOT NULL REFERENCES server_messages(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    PRIMARY KEY (message_id, user_id)
);

CREATE TABLE IF NOT EXISTS pm_pins (
    message_id UUID NOT NULL REFERENCES dm_messages(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL,
    receiver_id INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS server_pins (
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    message_id UUID NOT NULL REFERENCES server_messages(id) ON DELETE CASCADE,
    channel_id UUID REFERENCES server_channels(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS server_events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    event_topic VARCHAR(32) NOT NULL,
    event_description VARCHAR(1000) NOT NULL,
    event_repeat SMALLINT NOT NULL, -- 1 = does not repeat 2 = weekly on current day 3 = every other current day 4 = monthly on the first tuesday 5 = annually on sep 01 5 = every day 6 = every weekday (monday to friday)
    start_time TIMESTAMPTZ NOT NULL,
    end_time TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS server_events_interested (
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    server_event_id UUID NOT NULL REFERENCES server_events(id) ON DELETE CASCADE,
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS server_invites (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    created_by UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    code VARCHAR(32) NOT NULL UNIQUE,
    channel_id UUID REFERENCES server_channels(id) ON DELETE SET NULL,
    max_uses SMALLINT, -- 32000 = unlimited
    uses SMALLINT NOT NULL DEFAULT 0,
    expires_at TIMESTAMP, -- null = unlimited
    is_revoked BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS notifications (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sender_id INTEGER NOT NULL, -- sender id
    request_id INTEGER NOT NULL, -- request id
    type BOOLEAN NOT NULL, -- true = Friend Request false = Private Message
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS server_tag (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    server_id UUID NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    server_tag_id SMALLINT NOT NULL,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS friends (
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    friend_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    PRIMARY KEY (user_id, friend_id),

    CHECK (user_id < friend_id)
);

CREATE TABLE IF NOT EXISTS connections (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name VARCHAR(100) NOT NULL,
    url VARCHAR(255) NOT NULL,
    connection_type VARCHAR(50) DEFAULT 'none',
    statee VARCHAR(255) NOT NULL,
    visible BOOLEAN DEFAULT TRUE,
    refresh_token_ciphertext BYTEA NOT NULL,
    refresh_token_tag       BYTEA NOT NULL,
    refresh_token_nonce      BYTEA NOT NULL,
    access_token_ciphertext  BYTEA NOT NULL,
    access_token_tag         BYTEA NOT NULL,
    access_token_nonce       BYTEA NOT NULL,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_server_roles_scroll
    ON server_roles (server_id, user_id, position DESC, id);

CREATE INDEX IF NOT EXISTS idx_server_members_user
    ON server_members(user_id);

CREATE INDEX IF NOT EXISTS idx_server_members_server
    ON server_members(server_id);

CREATE INDEX IF NOT EXISTS idx_bans_guild_id
    ON server_bans (guild_id);

CREATE INDEX IF NOT EXISTS idx_bans_user_id
    ON server_bans (user_id);

CREATE INDEX IF NOT EXISTS idx_bans_guild_user
    ON server_bans (guild_id, user_id);

CREATE INDEX IF NOT EXISTS CONCURRENTLY idx_server_messages_created_at_id
    ON server_messages (created_at DESC, id DESC);