namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>
/// A field update failed and undoing it failed too, so the whole transaction was rolled
/// back. Its own type so the trigger dispatcher can tell it apart: with the transaction
/// gone, no failure policy can let the operation carry on.
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
