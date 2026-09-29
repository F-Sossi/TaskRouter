using System.ComponentModel.DataAnnotations;

namespace DemoDocuments.Server.Domain;

/// <summary>
/// Mirrors the original system's DocumentType. In the original system this enum is half the workflow key; here it
/// is purely the host's own concept and reaches the engine only as the string
/// SubjectType on a WorkflowSubject.
/// </summary>
public enum DemoDocumentType
{
    ChangeRequest,    // change request
    COP,    // Configuration Overhaul Planning
    DW      // Ship Installation Drawing
}

public enum DocumentStatus
{
    Scheduled,
    InProgress,
    Complete,
    Cancelled
}

public enum WorkItemStatus
{
    Estimated,
    InProgress,
    Complete
}

public enum LarUrgency
{
    Routine,
    Priority,
    Urgent
}

/// <summary>
/// Mirrors the original system's DocumentBase: a table-per-hierarchy root with the fields every
/// document shares, a WorkItems collection, and a DocumentType discriminator.
/// The engine never sees this type.
/// </summary>
public abstract class DocumentBase
{
    public int Id { get; set; }

    [Required] public required string Title { get; set; }
    [Required] public required string DocNumber { get; set; }

    public string? Revision { get; set; } = "-";
    public string? Description { get; set; }
    public string? Comments { get; set; }

    public DateTime? IssueDate { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? InternalDueDate { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Scheduled;

    public int? GroupId { get; set; }
    public Group? Group { get; set; }

    public ICollection<WorkItem> WorkItems { get; set; } = [];

    public string CreatorId { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }

    public abstract DemoDocumentType DocumentType { get; }
}

/// <summary>Mirrors the original system's ChangeRequest, trimmed to the fields that matter here.</summary>
public class ChangeRequest : DocumentBase
{
    public override DemoDocumentType DocumentType => DemoDocumentType.ChangeRequest;

    [Required] public required string Originator { get; set; }

    public DateTime DateEntered { get; set; } = DateTime.UtcNow;
    public DateTime? DateSent { get; set; }
    public DateTime? DateReplyRequiredBy { get; set; }
    public DateTime? DateReplyReceived { get; set; }

    public bool IsReplyRequired { get; set; }
    public bool IsClassChange { get; set; }
    public bool IsRepairChange { get; set; }

    public LarUrgency Urgency { get; set; } = LarUrgency.Routine;

    public string? CogCoordinatorActorId { get; set; }
}

/// <summary>A second document type, so the demo proves the engine is subject-agnostic.</summary>
public class Drawing : DocumentBase
{
    public override DemoDocumentType DocumentType => DemoDocumentType.DW;

    public string? DrawingNumber { get; set; }
    public string? SheetCount { get; set; }
}

/// <summary>Mirrors the original system's Group (the owning programme/project grouping).</summary>
public class Group
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

/// <summary>
/// Mirrors the original system's Section, including Section Lead and parent division — assignment routing is
/// the part most tightly coupled to the org model, so it is reproduced faithfully.
/// The section Code becomes the engine's opaque BranchKey.
/// </summary>
public class Section
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? SectionLeadActorId { get; set; }
    public int? DivisionId { get; set; }
    public Division? Division { get; set; }
}

public class Division
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? DivisionHeadActorId { get; set; }
}

/// <summary>Mirrors the original system's User, keyed by an opaque actor id rather than an a fixed-length government id.</summary>
public class Person
{
    public required string ActorId { get; set; }
    public required string FullName { get; set; }
    public int? SectionId { get; set; }
}

/// <summary>
/// Mirrors the original system's WorkItem, including the clamped CompletionPercentage that the
/// workflow's progress reporting drives.
/// </summary>
public class WorkItem
{
    public int Id { get; set; }

    public required int DocumentId { get; set; }
    public DocumentBase? Document { get; set; }

    public required int AssignedSectionId { get; set; }
    public Section? AssignedSection { get; set; }

    public string? CurrentlyAssignedActorId { get; set; }

    public WorkItemStatus WorkItemState { get; set; } = WorkItemStatus.Estimated;

    private double _completionPercentage;
    public double CompletionPercentage
    {
        get => _completionPercentage;
        set => _completionPercentage = Math.Clamp(value, 0, 100);
    }

    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }
}
