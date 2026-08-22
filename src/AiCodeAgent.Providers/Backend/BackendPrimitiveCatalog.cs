using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Providers.Backend;

/// <summary>
/// Catalog of available bundled backend primitives. The catalog is
/// extensible: adding a new primitive is just adding an entry (with optional
/// inline templates) to the constructor.
/// </summary>
public sealed class BackendPrimitiveCatalog
{
    private readonly Dictionary<string, BackendPrimitive> _primitives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<BackendPrimitiveCatalog>? _logger;

    public BackendPrimitiveCatalog(ILogger<BackendPrimitiveCatalog>? logger = null)
    {
        _logger = logger;

        Register(new BackendPrimitive
        {
            Id = "auth",
            Name = "Authentication",
            Description = "Login/register endpoints, JWT middleware, user table migration.",
            ConfigSchema =
            [
                new() { Name = "JWT_SECRET", Description = "Secret used to sign JWT tokens", Required = true },
                new() { Name = "JWT_EXPIRES_HOURS", Description = "Token lifetime in hours", DefaultValue = "24" }
            ],
            InlineTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["auth/auth.js"] = BackendTemplates.AuthJs,
                ["auth/routes.js"] = BackendTemplates.AuthRoutesJs
            },
            WiringSnippet = "app.use(authMiddleware); app.use('/api/auth', authRoutes);"
        });

        Register(new BackendPrimitive
        {
            Id = "db",
            Name = "Database",
            Description = "SQL connection helper, migration runner, seed script.",
            ConfigSchema =
            [
                new() { Name = "DB_CONNECTION_STRING", Description = "Primary database connection string", Required = true },
                new() { Name = "DB_POOL_SIZE", Description = "Connection pool size", DefaultValue = "10" }
            ],
            InlineTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["db/db.js"] = BackendTemplates.DbJs
            },
            WiringSnippet = "await runMigrations(db); app.locals.db = db;"
        });

        Register(new BackendPrimitive
        {
            Id = "storage",
            Name = "Storage",
            Description = "Upload/download endpoints, blob storage abstraction.",
            Dependencies = ["auth"],
            ConfigSchema =
            [
                new() { Name = "STORAGE_DRIVER", Description = "local|s3|azure|gcs", DefaultValue = "local" },
                new() { Name = "STORAGE_BUCKET", Description = "Bucket/container name" },
                new() { Name = "STORAGE_LOCAL_DIR", Description = "Local storage directory", DefaultValue = "./uploads" }
            ],
            InlineTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["storage/routes.js"] = BackendTemplates.StorageJs
            },
            WiringSnippet = "app.use('/api/storage', storageRoutes);"
        });

        Register(new BackendPrimitive
        {
            Id = "email",
            Name = "Email",
            Description = "SMTP/transactional email helper.",
            ConfigSchema =
            [
                new() { Name = "SMTP_HOST", Description = "SMTP server host", Required = true },
                new() { Name = "SMTP_PORT", Description = "SMTP server port", DefaultValue = "587" },
                new() { Name = "SMTP_USER", Description = "SMTP username" },
                new() { Name = "SMTP_PASS", Description = "SMTP password" },
                new() { Name = "EMAIL_FROM", Description = "From address", DefaultValue = "noreply@example.com" }
            ],
            InlineTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["email/email.js"] = BackendTemplates.EmailJs
            },
            WiringSnippet = "app.locals.mailer = createMailer(emailConfig);"
        });

        Register(new BackendPrimitive
        {
            Id = "payments",
            Name = "Payments",
            Description = "Checkout session, webhook handler, subscription model (stripe-style).",
            Dependencies = ["auth", "db"],
            ConfigSchema =
            [
                new() { Name = "PAYMENTS_PROVIDER", Description = "stripe|paddle|lemonsqueezy", DefaultValue = "stripe" },
                new() { Name = "PAYMENTS_SECRET_KEY", Description = "Provider secret API key", Required = true },
                new() { Name = "PAYMENTS_WEBHOOK_SECRET", Description = "Webhook signing secret", Required = true },
                new() { Name = "PAYMENTS_CURRENCY", Description = "Default currency", DefaultValue = "usd" }
            ],
            InlineTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["payments/payments.js"] = BackendTemplates.PaymentsJs
            },
            WiringSnippet = "app.post('/api/payments/webhook', paymentsWebhook);"
        });
    }

    public void Register(BackendPrimitive primitive)
    {
        if (primitive is null) throw new ArgumentNullException(nameof(primitive));
        _primitives[primitive.Id] = primitive;
    }

    public BackendPrimitive? Get(string id) =>
        _primitives.TryGetValue(id, out var p) ? p : null;

    public IEnumerable<BackendPrimitive> GetAll() => _primitives.Values;

    /// <summary>Resolves a list of primitive ids, including transitive dependencies.</summary>
    public IReadOnlyList<BackendPrimitive> Resolve(IReadOnlyList<string> ids, string? stack = null)
    {
        var ordered = new List<BackendPrimitive>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string id)
        {
            if (!visited.Add(id)) return;
            if (!_primitives.TryGetValue(id, out var p))
                throw new InvalidOperationException($"Unknown backend primitive: '{id}'");
            foreach (var dep in p.Dependencies) Visit(dep);
            ordered.Add(p);
        }

        foreach (var id in ids) Visit(id);
        return ordered;
    }
}