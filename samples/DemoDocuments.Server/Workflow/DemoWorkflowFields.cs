using System.Globalization;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;

using TaskRouter.Core.Fields;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Fields;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// The fields the demo lets workflow authors set.
///
/// <para>Two layers, which is the pattern a host with many document types follows. Every
/// document type gets the document's common fields and the two work-item targets. A
/// change request additionally gets its own fields, added to the same "This document"
/// target — so an author on a change-request workflow sees one target with more in it,
/// and an author on a drawing workflow never sees fields a drawing does not have.</para>
///
/// <para>A branch key in this demo is a section's <c>Code</c>, which is why the
/// branch work item is found by <c>AssignedSection.Code</c>. A host that keys branches
/// differently resolves differently; that is why resolution is the host's code.</para>
/// </summary>
public static class DemoWorkflowFields
{
    // The strings the demo registers as workflow subject types (see Program.cs). Literal
    // because "Drawing" is the subject type's name, while DemoDocumentType calls it DW.
    public static readonly string[] DocumentSubjectTypes =
        ["ChangeRequest", "Drawing"];

    public static void Configure(WorkflowFieldsBuilder fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        fields.ForSubjectTypes(DocumentSubjectTypes)
            .Target<DocumentBase>("document", "This document", FieldCardinality.One, context =>
            {
                var id = DocumentId(context.Subject);
                return context.Db<DemoDbContext>().Documents.Where(d => d.Id == id);
            })
                .Field(d => d.Status, "Status", write: (document, status, _, _) =>
                {
                    DemoDocumentStatusRules.MoveTo(document, status);
                    return Task.CompletedTask;
                })
                .Field(d => d.DueDate, "Due date")
                .Field(d => d.InternalDueDate, "Internal due date")
                .Field(d => d.Comments, "Comments")
            .Target<WorkItem>("branchWorkItem", "Work item for this task's section", FieldCardinality.One, context =>
            {
                var id = DocumentId(context.Subject);
                var section = context.Task.AssignedBranchKey;
                return context.Db<DemoDbContext>().WorkItems
                    .Where(w => w.DocumentId == id && !w.IsArchived && w.AssignedSection!.Code == section);
            })
                .Field(w => w.WorkItemState, "State")
                .Field(w => w.CurrentlyAssignedActorId, "Assigned to", FieldKind.Actor)
                .OnWritten(Touch)
            .Target<WorkItem>("allWorkItems", "All work items on this document", FieldCardinality.Many, context =>
            {
                var id = DocumentId(context.Subject);
                return context.Db<DemoDbContext>().WorkItems
                    .Where(w => w.DocumentId == id && !w.IsArchived);
            })
                .Field(w => w.WorkItemState, "State")
                .OnWritten(Touch);

        fields.ForSubjectType("ChangeRequest")
            .Extend<ChangeRequest>("document")
                .Field(c => c.Urgency, "Urgency")
                .Field(c => c.IsReplyRequired, "Reply required")
                .Field(c => c.DateReplyRequiredBy, "Reply required by")
                .Field(c => c.CogCoordinatorActorId, "Cog coordinator", FieldKind.Actor);
    }

    private static Task Touch(WorkItem item, FieldWriteContext context, CancellationToken ct)
    {
        item.Modified = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    // An unparseable id is a subject that is not one of our documents: resolve nothing,
    // and the update reports there was nothing to change.
    private static int DocumentId(WorkflowSubject subject) =>
        int.TryParse(subject.SubjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
}
