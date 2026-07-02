CREATE TABLE IF NOT EXISTS users (
    id SERIAL PRIMARY KEY,
    username VARCHAR(32) NOT NULL UNIQUE,
    display_name VARCHAR(64) NOT NULL,
    email VARCHAR(256) NOT NULL UNIQUE,
    password_hash TEXT NOT NULL,
    avatar_url TEXT,
    about_me VARCHAR(250),
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    last_online TIMESTAMP,
    is_verified BOOLEAN NOT NULL DEFAULT FALSE,
    is_banned SMALLINT NOT NULL DEFAULT 0 -- 0 = Fine 1 = Banned 2 = Account Deleted
);

CREATE TABLE IF NOT EXISTS dm_conversations (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    is_group BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS dm_conversation_members (
    conversation_id UUID NOT NULL REFERENCES dm_conversations(id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL,
    PRIMARY KEY (conversation_id, user_id)
);

CREATE TABLE IF NOT EXISTS dm_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    conversation_id UUID NOT NULL REFERENCES dm_conversations(id) ON DELETE CASCADE,
    sender_id INTEGER NOT NULL,
    message_content TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    edited BOOLEAN DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS server_messages (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    sender_id INTEGER NOT NULL,
    message_content TEXT,
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    edited BOOLEAN DEFAULT FALSE,
    private_message BOOLEAN DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS server_message_attachments (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    message_id UUID NOT NULL REFERENCES server_messages(id) ON DELETE CASCADE,
    url TEXT NOT NULL
);
