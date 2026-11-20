namespace RemoteDesk.Core.Models;

[Flags]
public enum SessionPermissions
{
    None = 0,
    View = 1 << 0,
    Control = 1 << 1,
    Clipboard = 1 << 2,
    Files = 1 << 3,
    Audio = 1 << 4,
    Terminal = 1 << 5,
    All = View | Control | Clipboard | Files | Audio | Terminal
}
