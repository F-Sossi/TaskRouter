using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Results;

namespace TaskRouter.AspNetCore;

/// <summary>
/// Turns the engine's <c>Result&lt;T&gt;</c> into an HTTP response.
///
/// The engine reports every refusal as a failed Result carrying an exception, so this is
/// the only place status codes are decided.
/// </summary>
internal sealed class WorkflowResultMapper(ILogger<WorkflowResultMapper> logger)
{
    internal IResult Map<T>(Result<T> result) =>
        result.Match(value => Results.Ok(value), MapError);

    internal IResult MapUnit(Result<Unit> result) =>
        result.Match(_ => Results.Ok(), MapError);

    private IResult MapError(Exception error) => error switch
    {
        // Order is load-bearing: denial -> absence -> bad input -> conflict ->
        // unexpected, narrowest to widest. WorkflowAuthorizationException and
        // WorkflowNotFoundException both derive from InvalidOperationException -- the
        // conflict arm would swallow them if it came first.
        WorkflowAuthorizationException denied => Results.Problem(
            detail: denied.Reason,
            statusCode: StatusCodes.Status403Forbidden,
            title: "Not authorized",
            extensions: new Dictionary<string, object?>
            {
                ["operation"] = denied.Operation.ToString(),
            }),

        WorkflowNotFoundException missing => Results.Problem(
            detail: missing.Message,
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found"),

        // Caller error, not server error. Semantically invalid payloads -- duplicate
        // branch keys, a fork of one branch, a missing array -- reach the engine and are
        // refused with an ArgumentException; only *syntactically* malformed JSON is
        // caught by ASP.NET before a handler runs. Answering 500 here would report the
        // caller's mistake as our fault and withhold the sentence that explains it.
        // ArgumentNullException derives from ArgumentException, so this covers both.
        ArgumentException invalid => Results.Problem(
            detail: invalid.Message,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid request"),

        // What is left after absence, denial and bad input are peeled off is
        // overwhelmingly "the workflow is not in a state where this is allowed" --
        // already completed, cancelled, superseded by a fork.
        InvalidOperationException conflict => Results.Problem(
            detail: conflict.Message,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict"),

        // The one place a message is withheld, deliberately: an unexpected exception is
        // the one class whose text was never written with a caller in mind.
        _ => LogAndHide(error),
    };

    private IResult LogAndHide(Exception error)
    {
        logger.LogError(error, "A workflow endpoint failed unexpectedly.");

        return Results.Problem(
            detail: "An unexpected error occurred.",
            statusCode: StatusCodes.Status500InternalServerError,
            title: "Error");
    }
}
