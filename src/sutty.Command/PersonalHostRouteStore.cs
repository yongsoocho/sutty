using sutty.Core.Models;
using sutty.Core.Routing;

namespace sutty.Command;

public enum PersonalRouteMode { Profile, Direct, Bastion }

public sealed record PersonalRouteChoice(PersonalRouteMode Mode, string? GatewayId = null);

/// <summary>Preferences live only in the current Windows user's database. Team exports read host_profiles.</summary>
public static class PersonalHostRouteStore
{
    public static PersonalRouteChoice Read(string hostId)
    {
        HostProfileStore.EnsureInitialized();
        using var db = Db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT mode, gateway_id FROM personal_host_routes WHERE host_id = $id";
        cmd.Parameters.AddWithValue("$id", hostId);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return new(PersonalRouteMode.Profile);
        return row.GetString(0) switch
        {
            "Direct" => new(PersonalRouteMode.Direct),
            "Bastion" when !row.IsDBNull(1) => new(PersonalRouteMode.Bastion, row.GetString(1)),
            _ => throw new InvalidDataException("Invalid personal route. Select a route again."),
        };
    }

    public static void Save(string hostId, PersonalRouteChoice choice)
    {
        var host = HostProfileStore.GetById(hostId) ?? throw new InvalidOperationException("Saved host no longer exists.");
        Validate(host, choice);
        if (choice.Mode == PersonalRouteMode.Bastion &&
            !BastionConnectionService.IsEligible(HostProfileStore.GetById(choice.GatewayId!)))
            throw new InvalidOperationException("The saved bastion is no longer available.");
        using var db = Db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = choice.Mode == PersonalRouteMode.Profile
            ? "DELETE FROM personal_host_routes WHERE host_id = $id"
            : """
              INSERT INTO personal_host_routes(host_id, mode, gateway_id)
              SELECT id, $mode, $gateway FROM host_profiles WHERE id = $id
              ON CONFLICT(host_id) DO UPDATE SET mode = excluded.mode, gateway_id = excluded.gateway_id
              """;
        cmd.Parameters.AddWithValue("$id", hostId);
        if (choice.Mode != PersonalRouteMode.Profile)
        {
            cmd.Parameters.AddWithValue("$mode", choice.Mode.ToString());
            cmd.Parameters.AddWithValue("$gateway", (object?)choice.GatewayId ?? DBNull.Value);
        }
        if (cmd.ExecuteNonQuery() == 0 && choice.Mode != PersonalRouteMode.Profile)
            throw new InvalidOperationException("Saved host no longer exists.");
    }

    public static void Validate(HostProfile host, PersonalRouteChoice choice)
    {
        if (host.IsExternalCommand || !host.Route.CanConnect)
            throw new InvalidOperationException("Review the saved SSH route before connecting.");
        if (choice.Mode == PersonalRouteMode.Direct && (host.Route.DisableDirect || host.Route.LegacyDisableDirect == true))
            throw new InvalidOperationException("This host requires an indirect route.");
        if (choice.Mode == PersonalRouteMode.Bastion &&
            (string.IsNullOrWhiteSpace(choice.GatewayId) || choice.GatewayId == host.Id))
            throw new InvalidOperationException("Choose a different saved gateway.");
        if (!Enum.IsDefined(choice.Mode)) throw new ArgumentOutOfRangeException(nameof(choice));
    }

    public static void Apply(HostProfile host, PersonalRouteChoice choice, SshConnectionInfo draft, BastionConnectionService gateways)
    {
        Validate(host, choice);
        if (choice.Mode == PersonalRouteMode.Profile) return;
        var route = choice.Mode == PersonalRouteMode.Direct
            ? new ConnectionRoute { Type = ConnectionRouteType.Direct }
            : gateways.Load(choice.GatewayId!).Route;
        draft.Route = route;
        draft.RoutePolicy = new ConnectionRoutePolicy { DisableDirect = choice.Mode == PersonalRouteMode.Bastion };
        draft.SaveProfile = false;
        draft.RememberCredential = false;
    }
}
