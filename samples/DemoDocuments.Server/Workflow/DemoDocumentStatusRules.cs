using DemoDocuments.Server.Domain;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Which status a document may move to from which — the demo's stand-in for the state
/// machine a real host already has.
///
/// <para>It exists so a workflow that tries to put a finished document on hold is refused
/// here, by the host, rather than the field update writing whatever it is told. That is
/// the point of a field having a writer: the host's rules still hold.</para>
/// </summary>
public static class DemoDocumentStatusRules
{
    private static readonly Dictionary<DocumentStatus, DocumentStatus[]> Allowed = new()
    {
        [DocumentStatus.Scheduled] = [DocumentStatus.InProgress, DocumentStatus.OnHold, DocumentStatus.Cancelled],
        [DocumentStatus.InProgress] = [DocumentStatus.Complete, DocumentStatus.OnHold, DocumentStatus.Cancelled],
        [DocumentStatus.OnHold] = [DocumentStatus.InProgress, DocumentStatus.Cancelled],
        [DocumentStatus.Complete] = [],
        [DocumentStatus.Cancelled] = []
    };

    public static void MoveTo(DocumentBase document, DocumentStatus target)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Status == target)
        {
            return;
        }

        if (!Allowed[document.Status].Contains(target))
        {
            throw new InvalidOperationException(
                $"{document.DocNumber} cannot move from {document.Status} to {target}.");
        }

        document.Status = target;
    }
}
