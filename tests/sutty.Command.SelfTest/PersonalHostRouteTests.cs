using sutty.Command;
using sutty.Core.Models;
using sutty.Core.Routing;

internal static class PersonalHostRouteTests
{
    public static void Run(Action<bool, string> assert, string directory)
    {
        var original = Db.PathOverride;
        var direct = new PersonalRouteChoice(PersonalRouteMode.Direct);
        var jump = new PersonalRouteChoice(PersonalRouteMode.Bastion, "gateway");
        HostProfile Seed(string id) => HostProfileStore.Save(new HostProfileDraft
        {
            DisplayName = id, Host = id + ".example", Username = "operator", AuthMethod = "Agent",
        }, id);
        try
        {
            foreach (var user in new[] { "route-A", "route-B" })
            {
                Db.PathOverride = Path.Combine(directory, user + ".db");
                var host = Seed("app01");
                Seed("gateway");
                PersonalHostRouteStore.Save(host.Id, user == "route-A" ? direct : jump);
                var updated = HostProfileStore.ToDraft(host);
                updated.DisplayName = "Shared application";
                HostProfileStore.Save(updated, host.Id);
                var exported = DefinitionSharingService.Export([HostProfileStore.GetById(host.Id)!], []);
                assert(!exported.Contains("gateway.example") && !exported.Contains("\"gateway\""),
                    "personal gateway is excluded from the shared target definition");
            }
            var loader = new BastionConnectionService(HostProfileStore.GetById, _ => null);
            var app = HostProfileStore.GetById("app01")!;
            var draft = new SshConnectionInfo { SavedHostId = app.Id, SaveProfile = true, RememberCredential = true };
            PersonalHostRouteStore.Apply(app, PersonalHostRouteStore.Read(app.Id), draft, loader);
            assert(draft.Route.Host == "gateway.example" && !draft.SaveProfile && !draft.RememberCredential &&
                   draft.SavedHostId == app.Id, "user B keeps a local bastion without overwriting the target");
            PersonalHostRouteStore.Apply(app, direct, draft, loader);
            assert(draft.Route.Type == ConnectionRouteType.Direct && PersonalHostRouteStore.Read(app.Id) == jump,
                "a one-time direct choice preserves B's default");
            HostProfileStore.Delete("gateway");
            bool blocked = false;
            try { PersonalHostRouteStore.Apply(app, jump, draft, loader); } catch (InvalidOperationException) { blocked = true; }
            assert(blocked, "a deleted personal gateway stops the launch");
            Db.PathOverride = Path.Combine(directory, "route-A.db");
            assert(PersonalHostRouteStore.Read(app.Id) == direct, "user A retains an independent preference");
            var strict = HostProfileStore.ToDraft(HostProfileStore.GetById(app.Id)!);
            strict.Route = new() { Type = "SshJump", Host = "required.example", Port = 22, Username = "ops", AuthMethod = "Agent", DisableDirect = true };
            var restricted = HostProfileStore.Save(strict, app.Id);
            blocked = false;
            try { PersonalHostRouteStore.Apply(restricted, direct, draft, loader); } catch (InvalidOperationException) { blocked = true; }
            assert(blocked, "updated shared policy takes precedence over a stored direct choice");
            PersonalHostRouteStore.Save(app.Id, new(PersonalRouteMode.Profile));
            assert(PersonalHostRouteStore.Read(app.Id).Mode == PersonalRouteMode.Profile, "clearing a preference restores the shared route");
        }
        finally { Db.PathOverride = original; }
    }
}
