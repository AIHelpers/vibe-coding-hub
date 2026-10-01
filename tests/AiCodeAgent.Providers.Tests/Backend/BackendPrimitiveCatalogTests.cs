using System;
using System.Linq;
using AiCodeAgent.Providers.Backend;
using Xunit;

namespace AiCodeAgent.Providers.Tests.Backend;

public class BackendPrimitiveCatalogTests
{
    [Fact]
    public void Catalog_Registers_All_BuiltIn_Primitives()
    {
        var catalog = new BackendPrimitiveCatalog();

        var ids = catalog.GetAll().Select(p => p.Id).OrderBy(i => i).ToArray();

        Assert.Equal(new[] { "auth", "db", "email", "payments", "storage" }, ids);
    }

    [Fact]
    public void Get_Returns_Registered_Primitive()
    {
        var catalog = new BackendPrimitiveCatalog();

        var auth = catalog.Get("auth");

        Assert.NotNull(auth);
        Assert.Equal("Authentication", auth!.Name);
    }

    [Fact]
    public void Get_Returns_Null_For_Unknown_Id()
    {
        var catalog = new BackendPrimitiveCatalog();

        Assert.Null(catalog.Get("nonexistent"));
    }

    [Fact]
    public void Resolve_Orders_Dependencies_First()
    {
        var catalog = new BackendPrimitiveCatalog();

        // payments depends on auth + db; ensure those come first
        var resolved = catalog.Resolve(["payments"]);

        var ids = resolved.Select(p => p.Id).ToArray();
        Assert.Equal(new[] { "auth", "db", "payments" }, ids);
    }

    [Fact]
    public void Resolve_Includes_Transitive_Dependencies()
    {
        var catalog = new BackendPrimitiveCatalog();

        // storage depends on auth
        var resolved = catalog.Resolve(["storage"]);

        var ids = resolved.Select(p => p.Id).ToArray();
        Assert.Equal(new[] { "auth", "storage" }, ids);
    }

    [Fact]
    public void Resolve_Throws_For_Unknown_Primitive()
    {
        var catalog = new BackendPrimitiveCatalog();

        Assert.Throws<InvalidOperationException>(() => catalog.Resolve(["nope"]));
    }

    [Fact]
    public void Resolve_Deduplicates_When_Dependency_Requested_Twice()
    {
        var catalog = new BackendPrimitiveCatalog();

        var resolved = catalog.Resolve(["auth", "db"]);

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void Register_Adds_New_Primitive()
    {
        var catalog = new BackendPrimitiveCatalog();

        catalog.Register(new BackendPrimitive
        {
            Id = "cache",
            Name = "Cache",
            Description = "In-memory cache"
        });

        Assert.NotNull(catalog.Get("cache"));
    }
}