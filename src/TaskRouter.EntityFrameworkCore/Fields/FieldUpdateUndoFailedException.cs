namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>
/// A field update failed and undoing it failed too, so the whole transaction was rolled
/// back. Its own type so the trigger dispatcher can tell it apart: with the transaction
/// gone, no failure policy can let the operation carry on.
///
/// <para>Must never be caught and continued past. The rollback happened on the server, so
/// any later statement on the connection runs in autocommit mode and lands for good; this
/// exception propagating is the only thing that stops a partial write.</para>
/// </summary>
internal sealed class FieldUpdateUndoFailedException : InvalidOperationException
{
    public FieldUpdateUndoFailedException()
    {
    }

    public FieldUpdateUndoFailedException(string message)
        : base(message)
    {
    }

    public FieldUpdateUndoFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
