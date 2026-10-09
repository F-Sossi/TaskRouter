namespace TaskRouter.Core.Abstractions;

/// <summary>A person the host can assign work to. The id is the engine's, the name is
/// the host's.</summary>
public sealed record ActorOption(string ActorId, string DisplayName);

/// <summary>An org unit a fork can branch across. The key is the engine's, the name is
/// the host's.</summary>
public sealed record BranchOption(string Key, string DisplayName);

/// <summary>
/// What one of the host's subjects is called and where it lives.
///
/// <para>Every field is nullable because a host may not be able to resolve a subject at all —
/// a deleted document, a subject type this host does not own — and the honest answer is "I do
/// not know" rather than a fabricated label. A caller that gets nulls falls back to the raw
/// subject key and renders the row unclickable, which keeps an orphaned task visible.</para>
///
/// <para><see cref="Url"/> is relative to the app's base, with no leading slash — see
/// <c>InboxItem.SubjectUrl</c> for why.</para>
/// </summary>
public sealed record SubjectDescriptor(string? Label, string? Subtitle, string? Url);
