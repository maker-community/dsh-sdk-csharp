using DeepSeek.Harness.Sdk;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Dsh.Sdk.CSharp.McpServer;

public sealed record McpConversation(
    string Id,
    string OwnerId,
    string Title,
    string DshSessionId,
    string NotificationEmail,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record McpJob(
    string Id,
    string ConversationId,
    string DshSessionId,
    string Prompt,
    string NotificationEmail,
    string Status,
    string? Result,
    string? Error,
    string EmailStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record McpOutboxMessage(
    string Id,
    string? JobId,
    string Recipient,
    string Subject,
    string Body,
    int AttemptCount);

public sealed record McpHostRun(
    string SessionId,
    string SessionTitle,
    string MessageId,
    string Response,
    string? FinishReason,
    string EmailStatus,
    DateTimeOffset CreatedAt);

public interface IMcpStateStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<McpConversation> GetOrCreateActiveSessionAsync(
        string ownerId,
        string email,
        CancellationToken cancellationToken);

    Task<McpConversation> CreateAndActivateSessionAsync(
        string ownerId,
        string email,
        string title,
        CancellationToken cancellationToken);

    Task<McpConversation> SwitchActiveSessionAsync(
        string ownerId,
        string email,
        string conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpConversation>> ListSessionsAsync(
        string ownerId,
        CancellationToken cancellationToken);

    Task<McpConversation?> FindSessionByDshSessionIdAsync(
        string dshSessionId,
        CancellationToken cancellationToken);

    Task<McpJob> SubmitJobAsync(
        string ownerId,
        string email,
        string prompt,
        CancellationToken cancellationToken);

    Task<McpJob?> GetJobAsync(
        string ownerId,
        string jobId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<McpHostRun>> GetRecentHostRunsAsync(
        string ownerId,
        int count,
        CancellationToken cancellationToken);

    Task<McpJob?> TryClaimNextJobAsync(CancellationToken cancellationToken);

    Task CompleteJobAsync(
        string jobId,
        HarnessRunResult result,
        CancellationToken cancellationToken);

    Task FailJobAsync(
        string jobId,
        string error,
        CancellationToken cancellationToken);

    Task<McpOutboxMessage?> TryClaimNextEmailAsync(CancellationToken cancellationToken);

    Task MarkEmailSentAsync(string outboxId, CancellationToken cancellationToken);

    Task MarkEmailFailedAsync(
        string outboxId,
        string error,
        CancellationToken cancellationToken);

    Task<bool> IsMcpJobRunningAsync(
        string dshSessionId,
        CancellationToken cancellationToken);

    Task<bool> PersistObservedHostResultAsync(
        string dshSessionId,
        string messageId,
        string response,
        string? finishReason,
        CancellationToken cancellationToken);
}

public sealed class SqliteMcpStateStore : IMcpStateStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<SqliteMcpStateStore> _logger;
    private readonly bool _emailNotificationsEnabled;
    private bool _initialized;

    public SqliteMcpStateStore(
        McpBridgeSettings settings,
        ILogger<SqliteMcpStateStore> logger)
    {
        _logger = logger;
        _emailNotificationsEnabled = settings.EmailNotificationsEnabled;
        var directory = Path.GetDirectoryName(settings.DatabasePath)
            ?? throw new InvalidOperationException("The MCP database path must have a parent directory.");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = settings.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;

                    CREATE TABLE IF NOT EXISTS conversations (
                        owner_id TEXT NOT NULL,
                        id TEXT NOT NULL,
                        title TEXT NOT NULL,
                        dsh_session_id TEXT NOT NULL UNIQUE,
                        notification_email TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        PRIMARY KEY (owner_id, id)
                    );

                    CREATE TABLE IF NOT EXISTS active_conversations (
                        owner_id TEXT NOT NULL PRIMARY KEY,
                        conversation_id TEXT NOT NULL,
                        FOREIGN KEY (owner_id, conversation_id)
                            REFERENCES conversations(owner_id, id)
                    );

                    CREATE TABLE IF NOT EXISTS jobs (
                        id TEXT NOT NULL PRIMARY KEY,
                        owner_id TEXT NOT NULL,
                        conversation_id TEXT NOT NULL,
                        dsh_session_id TEXT NOT NULL,
                        prompt TEXT NOT NULL,
                        notification_email TEXT NOT NULL,
                        status TEXT NOT NULL,
                        result TEXT NULL,
                        error TEXT NULL,
                        email_status TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_jobs_status_created
                        ON jobs(status, created_at);
                    CREATE INDEX IF NOT EXISTS ix_jobs_session_status
                        ON jobs(dsh_session_id, status);
                    CREATE INDEX IF NOT EXISTS ix_jobs_owner_created
                        ON jobs(owner_id, created_at DESC);

                    CREATE TABLE IF NOT EXISTS email_outbox (
                        id TEXT NOT NULL PRIMARY KEY,
                        dedupe_key TEXT NOT NULL UNIQUE,
                        job_id TEXT NULL,
                        owner_id TEXT NOT NULL,
                        recipient TEXT NOT NULL,
                        subject TEXT NOT NULL,
                        body TEXT NOT NULL,
                        status TEXT NOT NULL,
                        attempt_count INTEGER NOT NULL DEFAULT 0,
                        last_error TEXT NULL,
                        created_at TEXT NOT NULL,
                        sent_at TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS ix_email_outbox_status_created
                        ON email_outbox(status, created_at);

                    CREATE TABLE IF NOT EXISTS observed_host_runs (
                        dsh_session_id TEXT NOT NULL,
                        message_id TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (dsh_session_id, message_id)
                    );

                    CREATE TABLE IF NOT EXISTS host_run_results (
                        owner_id TEXT NOT NULL,
                        dsh_session_id TEXT NOT NULL,
                        session_title TEXT NOT NULL,
                        message_id TEXT NOT NULL,
                        response TEXT NOT NULL,
                        finish_reason TEXT NULL,
                        email_status TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (dsh_session_id, message_id)
                    );
                    CREATE INDEX IF NOT EXISTS ix_host_run_results_owner_created
                        ON host_run_results(owner_id, created_at DESC);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await RecoverAfterRestartAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<McpConversation> GetOrCreateActiveSessionAsync(
        string ownerId,
        string email,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var session = await GetActiveSessionCoreAsync(
                connection, ownerId, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                session = CreateConversation(ownerId, email, "Default");
                await InsertConversationAsync(connection, session, cancellationToken).ConfigureAwait(false);
                await SetActiveSessionCoreAsync(
                    connection, ownerId, session.Id, cancellationToken).ConfigureAwait(false);
                return session with { IsActive = true };
            }

            if (!string.IsNullOrWhiteSpace(email)
                && !string.Equals(session.NotificationEmail, email, StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTimeOffset.UtcNow;
                await UpdateSessionEmailAsync(
                    connection, ownerId, session.Id, email, now, cancellationToken).ConfigureAwait(false);
                session = session with { NotificationEmail = email, UpdatedAt = now };
            }

            await UpdateSessionLastUsedAsync(connection, ownerId, session.Id, cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<McpConversation> CreateAndActivateSessionAsync(
        string ownerId,
        string email,
        string title,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 80)
        {
            throw new ArgumentOutOfRangeException(nameof(title), "Session titles must not exceed 80 characters.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var session = CreateConversation(ownerId, email, title.Trim());
            await InsertConversationAsync(connection, session, cancellationToken).ConfigureAwait(false);
            await SetActiveSessionCoreAsync(connection, ownerId, session.Id, cancellationToken)
                .ConfigureAwait(false);
            return session with { IsActive = true };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<McpConversation> SwitchActiveSessionAsync(
        string ownerId,
        string email,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var session = await GetConversationCoreAsync(
                connection, ownerId, conversationId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The requested session does not exist for this caller.");
            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(email))
            {
                await UpdateSessionEmailAsync(
                    connection, ownerId, conversationId, email, now, cancellationToken).ConfigureAwait(false);
            }
            await SetActiveSessionCoreAsync(connection, ownerId, conversationId, cancellationToken)
                .ConfigureAwait(false);
            return session with
            {
                NotificationEmail = string.IsNullOrWhiteSpace(email) ? session.NotificationEmail : email,
                IsActive = true,
                UpdatedAt = now,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<McpConversation>> ListSessionsAsync(
        string ownerId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.id, c.owner_id, c.title, c.dsh_session_id, c.notification_email,
                       c.created_at, c.updated_at,
                       CASE WHEN a.conversation_id = c.id THEN 1 ELSE 0 END AS is_active
                FROM conversations c
                LEFT JOIN active_conversations a ON a.owner_id = c.owner_id
                WHERE c.owner_id = $owner
                ORDER BY c.updated_at DESC;
                """;
            command.Parameters.AddWithValue("$owner", ownerId);

            var sessions = new List<McpConversation>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessions.Add(ReadConversation(reader));
            }

            return sessions;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<McpConversation?> FindSessionByDshSessionIdAsync(
        string dshSessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.owner_id, c.title, c.dsh_session_id, c.notification_email,
                   c.created_at, c.updated_at,
                   CASE WHEN a.conversation_id = c.id THEN 1 ELSE 0 END
            FROM conversations c
            LEFT JOIN active_conversations a ON a.owner_id = c.owner_id
            WHERE c.dsh_session_id = $session
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", dshSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadConversation(reader)
            : null;
    }

    public async Task<McpJob> SubmitJobAsync(
        string ownerId,
        string email,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var session = await GetActiveSessionCoreAsync(
                connection, ownerId, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                session = CreateConversation(ownerId, email, "Default");
                await InsertConversationAsync(connection, session, cancellationToken).ConfigureAwait(false);
                await SetActiveSessionCoreAsync(connection, ownerId, session.Id, cancellationToken)
                    .ConfigureAwait(false);
                session = session with { IsActive = true };
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(email))
                {
                    var sessionUpdatedAt = DateTimeOffset.UtcNow;
                    await UpdateSessionEmailAsync(
                        connection, ownerId, session.Id, email, sessionUpdatedAt, cancellationToken).ConfigureAwait(false);
                    session = session with { NotificationEmail = email, UpdatedAt = sessionUpdatedAt };
                }
            }

            var now = DateTimeOffset.UtcNow;
            var job = new McpJob(
                Guid.NewGuid().ToString("N"),
                session.Id,
                session.DshSessionId,
                prompt,
                email,
                "queued",
                null,
                null,
                _emailNotificationsEnabled ? "pending" : "disabled",
                now,
                now);

            await InsertJobAsync(connection, job, ownerId, cancellationToken).ConfigureAwait(false);
            await UpdateSessionLastUsedAsync(connection, ownerId, session.Id, cancellationToken)
                .ConfigureAwait(false);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<McpJob?> GetJobAsync(
        string ownerId,
        string jobId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, conversation_id, dsh_session_id, prompt, notification_email,
                       status, result, error, email_status, created_at, updated_at
                FROM jobs
                WHERE owner_id = $owner AND id = $id;
                """;
            command.Parameters.AddWithValue("$owner", ownerId);
            command.Parameters.AddWithValue("$id", jobId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadJob(reader)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<McpHostRun>> GetRecentHostRunsAsync(
        string ownerId,
        int count,
        CancellationToken cancellationToken)
    {
        if (count is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be between 1 and 50.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dsh_session_id, session_title, message_id, response,
                   finish_reason, email_status, created_at
            FROM host_run_results
            WHERE owner_id = $owner
            ORDER BY created_at DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$count", count);

        var hostRuns = new List<McpHostRun>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hostRuns.Add(new McpHostRun(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                ParseDate(reader.GetString(6))));
        }

        return hostRuns;
    }

    public async Task<McpJob?> TryClaimNextJobAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, conversation_id, dsh_session_id, prompt, notification_email,
                       status, result, error, email_status, created_at, updated_at
                FROM jobs
                WHERE status = 'queued'
                ORDER BY created_at
                LIMIT 1;
                """;
            McpJob? job;
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                job = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? ReadJob(reader)
                    : null;
            }

            if (job is null)
            {
                transaction.Commit();
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs SET status = 'running', updated_at = $now
                WHERE id = $id AND status = 'queued';
                """;
            update.Parameters.AddWithValue("$id", job.Id);
            update.Parameters.AddWithValue("$now", FormatDate(now));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                transaction.Rollback();
                return null;
            }

            transaction.Commit();
            return job with { Status = "running", UpdatedAt = now };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteJobAsync(
        string jobId,
        HarnessRunResult result,
        CancellationToken cancellationToken)
    {
        var response = result.FinalResponse;
        var body = $"""
            DSH task completed.
            Job: {jobId}
            Session: {result.SessionId}
            Finish reason: {result.FinishReason ?? "(not reported)"}

            {response}
            """;

        await CompleteJobCoreAsync(
            jobId,
            "completed",
            response,
            null,
            $"DSH task completed ({result.FinishReason ?? "unknown"})",
            body,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task FailJobAsync(
        string jobId,
        string error,
        CancellationToken cancellationToken)
    {
        await CompleteJobCoreAsync(
            jobId,
            "failed",
            null,
            error,
            "DSH task failed",
            $"DSH task {jobId} failed.{Environment.NewLine}{error}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpOutboxMessage?> TryClaimNextEmailAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, job_id, recipient, subject, body, attempt_count
                FROM email_outbox
                WHERE status = 'pending'
                ORDER BY created_at
                LIMIT 1;
                """;
            McpOutboxMessage? message;
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                message = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? new McpOutboxMessage(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetInt32(5))
                    : null;
            }

            if (message is null)
            {
                transaction.Commit();
                return null;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE email_outbox
                SET status = 'sending', attempt_count = attempt_count + 1
                WHERE id = $id AND status = 'pending';
                """;
            update.Parameters.AddWithValue("$id", message.Id);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                transaction.Rollback();
                return null;
            }

            transaction.Commit();
            return message with { AttemptCount = message.AttemptCount + 1 };
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkEmailSentAsync(string outboxId, CancellationToken cancellationToken) =>
        UpdateEmailStatusAsync(outboxId, "sent", null, cancellationToken);

    public Task MarkEmailFailedAsync(
        string outboxId,
        string error,
        CancellationToken cancellationToken) =>
        UpdateEmailStatusAsync(outboxId, "failed", error, cancellationToken);

    public async Task<bool> IsMcpJobRunningAsync(
        string dshSessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM jobs
                WHERE dsh_session_id = $session AND status = 'running');
            """;
        command.Parameters.AddWithValue("$session", dshSessionId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    public async Task<bool> PersistObservedHostResultAsync(
        string dshSessionId,
        string messageId,
        string response,
        string? finishReason,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = """
                SELECT owner_id, id, notification_email, title
                FROM conversations
                WHERE dsh_session_id = $session
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$session", dshSessionId);
            string ownerId;
            string conversationId;
            string email;
            string title;
            await using (var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    transaction.Commit();
                    return false;
                }

                ownerId = reader.GetString(0);
                conversationId = reader.GetString(1);
                email = reader.GetString(2);
                title = reader.GetString(3);
            }

            await using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = """
                INSERT OR IGNORE INTO observed_host_runs(dsh_session_id, message_id, created_at)
                VALUES ($session, $message, $now);
                """;
            mark.Parameters.AddWithValue("$session", dshSessionId);
            mark.Parameters.AddWithValue("$message", messageId);
            mark.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
            if (await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                transaction.Commit();
                return false;
            }

            var subject = $"DSH host task completed: {title}";
            var body = $"""
                A DSH host-originated run completed in session '{title}'.
                Session: {dshSessionId}
                Message: {messageId}
                Finish reason: {finishReason ?? "(not reported)"}

                {response}
                """;
            var emailStatus = !_emailNotificationsEnabled
                ? "disabled"
                : string.IsNullOrWhiteSpace(email)
                    ? "no_recipient"
                    : "queued";

            await using (var insertHostRun = connection.CreateCommand())
            {
                insertHostRun.Transaction = transaction;
                insertHostRun.CommandText = """
                    INSERT INTO host_run_results(
                        owner_id, dsh_session_id, session_title, message_id, response,
                        finish_reason, email_status, created_at)
                    VALUES ($owner, $session, $title, $message, $response,
                        $finishReason, $emailStatus, $createdAt);
                    """;
                insertHostRun.Parameters.AddWithValue("$owner", ownerId);
                insertHostRun.Parameters.AddWithValue("$session", dshSessionId);
                insertHostRun.Parameters.AddWithValue("$title", title);
                insertHostRun.Parameters.AddWithValue("$message", messageId);
                insertHostRun.Parameters.AddWithValue("$response", response);
                insertHostRun.Parameters.AddWithValue("$finishReason", (object?)finishReason ?? DBNull.Value);
                insertHostRun.Parameters.AddWithValue("$emailStatus", emailStatus);
                insertHostRun.Parameters.AddWithValue("$createdAt", FormatDate(DateTimeOffset.UtcNow));
                await insertHostRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (emailStatus == "queued")
            {
                await InsertOutboxAsync(
                    connection,
                    transaction,
                    $"host:{dshSessionId}:{messageId}",
                    null,
                    ownerId,
                    email,
                    subject,
                    body,
                    cancellationToken).ConfigureAwait(false);
            }
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE conversations SET updated_at = $now WHERE owner_id = $owner AND id = $id;";
            update.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$owner", ownerId);
            update.Parameters.AddWithValue("$id", conversationId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CompleteJobCoreAsync(
        string jobId,
        string status,
        string? result,
        string? error,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = """
                SELECT owner_id, notification_email
                FROM jobs WHERE id = $id;
                """;
            find.Parameters.AddWithValue("$id", jobId);
            string ownerId;
            string email;
            await using (var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new KeyNotFoundException($"Job '{jobId}' was not found.");
                }

                ownerId = reader.GetString(0);
                email = reader.GetString(1);
            }

            var now = DateTimeOffset.UtcNow;
            var emailStatus = _emailNotificationsEnabled ? "queued" : "disabled";
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs
                SET status = $status, result = $result, error = $error,
                    email_status = $emailStatus, updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
            update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            update.Parameters.AddWithValue("$emailStatus", emailStatus);
            update.Parameters.AddWithValue("$now", FormatDate(now));
            update.Parameters.AddWithValue("$id", jobId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (_emailNotificationsEnabled)
            {
                await InsertOutboxAsync(
                    connection,
                    transaction,
                    $"job:{jobId}",
                    jobId,
                    ownerId,
                    email,
                    subject,
                    body,
                    cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateEmailStatusAsync(
        string outboxId,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            string? jobId;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT job_id FROM email_outbox WHERE id = $id;";
                select.Parameters.AddWithValue("$id", outboxId);
                var value = await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                jobId = value is null or DBNull ? null : (string)value;
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE email_outbox
                    SET status = $status, last_error = $error,
                        sent_at = CASE WHEN $status = 'sent' THEN $now ELSE sent_at END
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$status", status);
                update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                update.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
                update.Parameters.AddWithValue("$id", outboxId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new KeyNotFoundException($"Email outbox item '{outboxId}' was not found.");
                }
            }

            if (jobId is not null)
            {
                await using var updateJob = connection.CreateCommand();
                updateJob.Transaction = transaction;
                updateJob.CommandText = """
                    UPDATE jobs SET email_status = $status, updated_at = $now WHERE id = $jobId;
                    """;
                updateJob.Parameters.AddWithValue("$status", status);
                updateJob.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
                updateJob.Parameters.AddWithValue("$jobId", jobId);
                await updateJob.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RecoverAfterRestartAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        var interrupted = new List<(string Id, string Owner, string Email)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, owner_id, notification_email
                FROM jobs WHERE status = 'running';
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                interrupted.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var job in interrupted)
        {
            const string error =
                "The MCP service stopped while this task was running. Its DSH outcome is unknown; it was not automatically replayed.";
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs SET status = 'interrupted', error = $error,
                    email_status = $emailStatus, updated_at = $now
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$error", error);
            update.Parameters.AddWithValue(
                "$emailStatus",
                _emailNotificationsEnabled ? "queued" : "disabled");
            update.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$id", job.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (_emailNotificationsEnabled)
            {
                await InsertOutboxAsync(
                    connection,
                    transaction,
                    $"job-interrupted:{job.Id}",
                    job.Id,
                    job.Owner,
                    job.Email,
                    "DSH task interrupted",
                    $"Task {job.Id} was interrupted by an MCP service restart. Its outcome is unknown and it was not automatically replayed.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var recoverOutbox = connection.CreateCommand())
        {
            recoverOutbox.Transaction = transaction;
            recoverOutbox.CommandText = """
                UPDATE email_outbox SET status = 'pending'
                WHERE status = 'sending';
                """;
            await recoverOutbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        if (interrupted.Count > 0)
        {
            _logger.LogWarning(
                "Marked {JobCount} interrupted DSH job(s) after MCP service restart; they were not replayed.",
                interrupted.Count);
        }
    }

    private static async Task InsertOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string dedupeKey,
        string? jobId,
        string ownerId,
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO email_outbox(
                id, dedupe_key, job_id, owner_id, recipient, subject, body, status, created_at)
            VALUES ($id, $dedupe, $job, $owner, $recipient, $subject, $body, 'pending', $created);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$dedupe", dedupeKey);
        command.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$recipient", recipient);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$created", FormatDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertConversationAsync(
        SqliteConnection connection,
        McpConversation session,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations(
                owner_id, id, title, dsh_session_id, notification_email, created_at, updated_at)
            VALUES ($owner, $id, $title, $dsh, $email, $created, $updated);
            """;
        command.Parameters.AddWithValue("$owner", session.OwnerId);
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue("$dsh", session.DshSessionId);
        command.Parameters.AddWithValue("$email", session.NotificationEmail);
        command.Parameters.AddWithValue("$created", FormatDate(session.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDate(session.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertJobAsync(
        SqliteConnection connection,
        McpJob job,
        string ownerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(
                id, owner_id, conversation_id, dsh_session_id, prompt, notification_email,
                status, result, error, email_status, created_at, updated_at)
            VALUES ($id, $owner, $conversation, $session, $prompt, $email,
                $status, NULL, NULL, $emailStatus, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$conversation", job.ConversationId);
        command.Parameters.AddWithValue("$session", job.DshSessionId);
        command.Parameters.AddWithValue("$prompt", job.Prompt);
        command.Parameters.AddWithValue("$email", job.NotificationEmail);
        command.Parameters.AddWithValue("$status", job.Status);
        command.Parameters.AddWithValue("$emailStatus", job.EmailStatus);
        command.Parameters.AddWithValue("$created", FormatDate(job.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatDate(job.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetActiveSessionCoreAsync(
        SqliteConnection connection,
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO active_conversations(owner_id, conversation_id)
            VALUES ($owner, $conversation)
            ON CONFLICT(owner_id) DO UPDATE SET conversation_id = excluded.conversation_id;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<McpConversation?> GetActiveSessionCoreAsync(
        SqliteConnection connection,
        string ownerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.owner_id, c.title, c.dsh_session_id, c.notification_email,
                   c.created_at, c.updated_at, 1
            FROM active_conversations a
            JOIN conversations c
              ON c.owner_id = a.owner_id AND c.id = a.conversation_id
            WHERE a.owner_id = $owner;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadConversation(reader)
            : null;
    }

    private static async Task<McpConversation?> GetConversationCoreAsync(
        SqliteConnection connection,
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.owner_id, c.title, c.dsh_session_id, c.notification_email,
                   c.created_at, c.updated_at,
                   CASE WHEN a.conversation_id = c.id THEN 1 ELSE 0 END
            FROM conversations c
            LEFT JOIN active_conversations a ON a.owner_id = c.owner_id
            WHERE c.owner_id = $owner AND c.id = $id;
            """;
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadConversation(reader)
            : null;
    }

    private static async Task UpdateSessionEmailAsync(
        SqliteConnection connection,
        string ownerId,
        string conversationId,
        string email,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE conversations SET notification_email = $email, updated_at = $now
            WHERE owner_id = $owner AND id = $id;
            """;
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$now", FormatDate(updatedAt));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateSessionLastUsedAsync(
        SqliteConnection connection,
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE conversations SET updated_at = $now WHERE owner_id = $owner AND id = $id;
            """;
        command.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static McpConversation CreateConversation(string ownerId, string email, string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpConversation(
            Guid.NewGuid().ToString("N"),
            ownerId,
            title,
            $"dsh-mcp-{Guid.NewGuid():N}",
            email,
            true,
            now,
            now);
    }

    private static McpConversation ReadConversation(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(7) != 0,
            ParseDate(reader.GetString(5)),
            ParseDate(reader.GetString(6)));

    private static McpJob ReadJob(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            ParseDate(reader.GetString(9)),
            ParseDate(reader.GetString(10)));

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
