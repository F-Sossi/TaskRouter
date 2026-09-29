namespace TaskRouter.Tests;

/// <summary>
/// Pins that no route handler in TaskRouter.AspNetCore calls
/// <c>IWorkflowEndpointActorAccessor.GetActorId</c> directly. Every handler that needs an
/// actor is supposed to go through <c>WorkflowEndpointActor.Resolve</c> instead, because
/// that is the one place the reserved-prefix guard lives -- a handler that called the
/// accessor on its own would compile, look correct, and silently skip the guard.
///
/// This is a source scan rather than a runtime check because there is no way to observe
/// "did the guard run" from outside; the only place the invariant is visible is the text
/// of the handlers themselves.
/// </summary>
[TestClass]
public class ActorChokepointGuardTests
{
    [TestMethod]
    public void No_handler_calls_GetActorId_outside_the_chokepoint()
    {
        var sourceDir = FindWorkflowAspNetCoreSourceDirectory();

        // The interface declaration and WorkflowEndpointActor's own call into the
        // accessor both legitimately contain the token "GetActorId(" -- they are the
        // chokepoint and its contract, not a bypass of it.
        var exempt = new[] { "IWorkflowEndpointActorAccessor.cs", "WorkflowEndpointActor.cs" };

        var offenders = Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !exempt.Contains(Path.GetFileName(path)))
            .Where(path => File.ReadAllText(path).Contains("GetActorId("))
            .Select(Path.GetFileName)
            .ToList();

        Assert.IsEmpty(offenders,
            "These files call GetActorId directly instead of going through " +
            $"WorkflowEndpointActor.Resolve: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Walks up from the test assembly's build output looking for src/TaskRouter.AspNetCore.
    /// This only works because the test project builds inside the repo tree at a fixed
    /// depth below the root -- true today for every configuration this repo builds with,
    /// but not a guarantee dotnet makes. If a future build layout moves output outside
    /// the tree, this throws a clear "could not locate" failure rather than silently
    /// scanning nothing and passing for the wrong reason.
    /// </summary>
    private static string FindWorkflowAspNetCoreSourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TaskRouter.AspNetCore");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new AssertFailedException(
            "Could not locate src/TaskRouter.AspNetCore above the test assembly's output " +
            $"directory ({AppContext.BaseDirectory}). This guard only works when the " +
            "test assembly builds inside the repo tree.");
    }
}
