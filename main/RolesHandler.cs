
using System;
using System.Security;

namespace Internal.Roles;

[Flags]
public enum Permissions : long
{
    None = 0,
    ViewChannel          = 1L << 0,
    SendMessages         = 1L << 1,
    SendTtsMessages      = 1L << 2,
    ManageMessages       = 1L << 3,
    EmbedLinks           = 1L << 4,
    AttachFiles          = 1L << 5,
    ReadMessageHistory   = 1L << 6,
    MentionEveryone      = 1L << 7,
    UseExternalEmojis    = 1L << 8,
    AddReactions         = 1L << 9,


    ManageChannels       = 1L << 10,
    ManageRoles          = 1L << 11,
    ManageServer         = 1L << 12,
    CreateInvites        = 1L << 13,
    KickMembers          = 1L << 14,
    BanMembers           = 1L << 15,
    TimeoutMembers       = 1L << 16,


    Connect              = 1L << 17,
    Speak                = 1L << 18,
    Video                = 1L << 19,
    MuteMembers          = 1L << 20,
    DeafenMembers        = 1L << 21,
    MoveMembers          = 1L << 22,
    UseVoiceActivity     = 1L << 23,
    PrioritySpeaker      = 1L << 24,


    ChangeNickname       = 1L << 25,
    ManageNicknames      = 1L << 26,
    UseSlashCommands     = 1L << 27,
    RequestToSpeak       = 1L << 28,

    Administrator        = 1L << 30
}
