using System.Diagnostics;
using System.Reflection;
using sutty.Core.Diagnostics;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Sessions;

internal static class RouteCredentialLifetimeTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static async Task RunAsync()
    {
        foreach (var type in new[]
                 {
                     ConnectionRouteType.HttpConnect,
                     ConnectionRouteType.Socks4,
                     ConnectionRouteType.Socks5,
                     ConnectionRouteType.SshJump,
                 })
        {
            var info = CreateInfo(type);
            var session = new SshNetSession(info);
            var route = GetField<ResolvedConnectionRoute>(session, "_route");
            Assert(route.Password.Length == 0 && route.Passphrase.Length == 0,
                "resolved session route metadata must not retain credentials");
            Assert(!route.ToString().Contains(info.Route.Password, StringComparison.Ordinal) &&
                   !route.ToString().Contains(info.Route.Passphrase, StringComparison.Ordinal),
                "resolved route formatting must not reveal credentials");
            Assert(GetField<string>(session, "_routePassword") == info.Route.Password &&
                   GetField<string>(session, "_routePassphrase") == info.Route.Passphrase,
                "session keeps clearable route credentials for active authentication");

            var password = info.Route.Password;
            info.ClearTransientSecrets();
            if (type != ConnectionRouteType.SshJump)
            {
                var connection = (Renci.SshNet.ConnectionInfo)Invoke(
                    session, "BuildConnectionInfo", CancellationToken.None)!;
                Assert(connection.ProxyPassword == password,
                    "proxy authentication survives caller draft clearing");
            }
            Assert(GetField<string>(session, "_routePassword") == password,
                "active route reconnect credentials survive caller draft clearing");

            typeof(SshNetSession).GetProperty(nameof(SshNetSession.State))!
                .SetValue(session, SessionState.Connected);
            await session.DisconnectAsync();
            AssertSecretsCleared(session);
        }

        var cancelled = new SshNetSession(CreateInfo(ConnectionRouteType.SshJump));
        await (Task)Invoke(
            cancelled,
            "CompleteCancelledConnectionAttemptAsync",
            new OperationCanceledException("Synthetic jump cancellation"),
            ConnectionDiagnosticStage.ProxyOrJumpRoute,
            Stopwatch.GetTimestamp(),
            false)!;
        Assert(cancelled.State == SessionState.Disconnected,
            "cancelled jump connection reaches disconnected state");
        AssertSecretsCleared(cancelled);
        cancelled.Info.ClearTransientSecrets();
    }

    public static void AssertSecretsCleared(SshNetSession session)
    {
        foreach (var field in new[] { "_password", "_passphrase", "_routePassword", "_routePassphrase" })
            Assert(GetField<string>(session, field).Length == 0,
                "completed session cleanup clears transient authentication fields");
        var route = GetField<ResolvedConnectionRoute>(session, "_route");
        Assert(route.Password.Length == 0 && route.Passphrase.Length == 0,
            "session cleanup leaves no credentials in the resolved route");
    }

    private static SshConnectionInfo CreateInfo(ConnectionRouteType type) => new()
    {
        Host = "target.example",
        Username = "target-user",
        Password = "synthetic-target-password",
        Passphrase = "synthetic-target-passphrase",
        Route = new ConnectionRoute
        {
            Type = type,
            Host = "route.example",
            Port = type == ConnectionRouteType.SshJump ? 22 : 1080,
            Username = "route-user",
            Password = "synthetic-route-password",
            Passphrase = "synthetic-route-passphrase",
        },
    };

    private static T GetField<T>(SshNetSession session, string name) =>
        (T)typeof(SshNetSession).GetField(name, PrivateInstance)!.GetValue(session)!;

    private static object? Invoke(SshNetSession session, string name, params object[] args) =>
        typeof(SshNetSession).GetMethod(name, PrivateInstance)!.Invoke(session, args);

    private static void Assert(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }
}
