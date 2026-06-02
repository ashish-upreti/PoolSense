using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PoolSense.Api.Logging;

public sealed class SqlServerApplicationLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const int QueueCapacity = 5000;
    private readonly Channel<ApplicationRunLogEntry> _channel;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ConcurrentDictionary<string, SqlServerApplicationLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;
    private readonly string _connectionString;
    private readonly string _applicationName;
    private readonly string _environmentName;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public SqlServerApplicationLoggerProvider(IConfiguration configuration, string applicationName, string environmentName)
    {
        _connectionString = configuration.GetConnectionString("PoolSenseSqlServer")
            ?? configuration.GetConnectionString("SqlServer")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? configuration.GetConnectionString("TicketSourceSqlServer")
            ?? string.Empty;
        _applicationName = applicationName;
        _environmentName = environmentName;
        _channel = Channel.CreateBounded<ApplicationRunLogEntry>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(ProcessQueueAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, category => new SqlServerApplicationLogger(category, this));
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _cancellationTokenSource.Cancel();

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _cancellationTokenSource.Dispose();
    }

    private void Enqueue(ApplicationRunLogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            _channel.Writer.TryWrite(entry);
        }
    }

    private async Task ProcessQueueAsync()
    {
        var cancellationToken = _cancellationTokenSource.Token;

        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await WriteAsync(entry, cancellationToken);
                }
                catch
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WriteAsync(ApplicationRunLogEntry entry, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        const string sql = """
            INSERT INTO dbo.application_run_logs (
                created_at,
                level,
                category,
                event_id,
                event_name,
                message,
                exception_type,
                exception_message,
                exception_stack_trace,
                state_json,
                scopes_json,
                machine_name,
                user_name,
                process_id,
                thread_id,
                environment_name,
                application_name)
            VALUES (
                @createdAt,
                @level,
                @category,
                @eventId,
                @eventName,
                @message,
                @exceptionType,
                @exceptionMessage,
                @exceptionStackTrace,
                @stateJson,
                @scopesJson,
                @machineName,
                @userName,
                @processId,
                @threadId,
                @environmentName,
                @applicationName);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@createdAt", entry.CreatedAt);
        command.Parameters.AddWithValue("@level", entry.Level);
        command.Parameters.AddWithValue("@category", entry.Category);
        command.Parameters.AddWithValue("@eventId", entry.EventId);
        command.Parameters.AddWithValue("@eventName", entry.EventName);
        command.Parameters.AddWithValue("@message", entry.Message);
        command.Parameters.AddWithValue("@exceptionType", entry.ExceptionType);
        command.Parameters.AddWithValue("@exceptionMessage", entry.ExceptionMessage);
        command.Parameters.AddWithValue("@exceptionStackTrace", entry.ExceptionStackTrace);
        command.Parameters.AddWithValue("@stateJson", entry.StateJson);
        command.Parameters.AddWithValue("@scopesJson", entry.ScopesJson);
        command.Parameters.AddWithValue("@machineName", Environment.MachineName);
        command.Parameters.AddWithValue("@userName", Environment.UserName);
        command.Parameters.AddWithValue("@processId", Environment.ProcessId);
        command.Parameters.AddWithValue("@threadId", Environment.CurrentManagedThreadId);
        command.Parameters.AddWithValue("@environmentName", _environmentName);
        command.Parameters.AddWithValue("@applicationName", _applicationName);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.application_run_logs', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.application_run_logs (
                    id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_application_run_logs PRIMARY KEY,
                    created_at datetime2(7) NOT NULL CONSTRAINT DF_application_run_logs_created_at DEFAULT SYSUTCDATETIME(),
                    level nvarchar(32) NOT NULL,
                    category nvarchar(256) NOT NULL,
                    event_id int NOT NULL CONSTRAINT DF_application_run_logs_event_id DEFAULT 0,
                    event_name nvarchar(256) NOT NULL CONSTRAINT DF_application_run_logs_event_name DEFAULT '',
                    message nvarchar(max) NOT NULL,
                    exception_type nvarchar(512) NOT NULL CONSTRAINT DF_application_run_logs_exception_type DEFAULT '',
                    exception_message nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_exception_message DEFAULT '',
                    exception_stack_trace nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_exception_stack_trace DEFAULT '',
                    state_json nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_state_json DEFAULT '',
                    scopes_json nvarchar(max) NOT NULL CONSTRAINT DF_application_run_logs_scopes_json DEFAULT '',
                    machine_name nvarchar(128) NOT NULL CONSTRAINT DF_application_run_logs_machine_name DEFAULT '',
                    user_name nvarchar(256) NOT NULL CONSTRAINT DF_application_run_logs_user_name DEFAULT '',
                    process_id int NOT NULL CONSTRAINT DF_application_run_logs_process_id DEFAULT 0,
                    thread_id int NOT NULL CONSTRAINT DF_application_run_logs_thread_id DEFAULT 0,
                    environment_name nvarchar(128) NOT NULL CONSTRAINT DF_application_run_logs_environment_name DEFAULT '',
                    application_name nvarchar(128) NOT NULL CONSTRAINT DF_application_run_logs_application_name DEFAULT ''
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_application_run_logs_created_at' AND object_id = OBJECT_ID(N'dbo.application_run_logs'))
                CREATE INDEX IX_application_run_logs_created_at ON dbo.application_run_logs (created_at DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_application_run_logs_level_created_at' AND object_id = OBJECT_ID(N'dbo.application_run_logs'))
                CREATE INDEX IX_application_run_logs_level_created_at ON dbo.application_run_logs (level, created_at DESC);
            """;

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class SqlServerApplicationLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly SqlServerApplicationLoggerProvider _provider;

        public SqlServerApplicationLogger(string categoryName, SqlServerApplicationLoggerProvider provider)
        {
            _categoryName = categoryName;
            _provider = provider;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return _provider._scopeProvider.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            var scopes = new List<object?>();
            _provider._scopeProvider.ForEachScope((scope, list) => list.Add(scope), scopes);

            _provider.Enqueue(new ApplicationRunLogEntry
            {
                CreatedAt = DateTime.UtcNow,
                Level = logLevel.ToString(),
                Category = _categoryName,
                EventId = eventId.Id,
                EventName = eventId.Name ?? string.Empty,
                Message = RedactSensitiveText(message),
                ExceptionType = exception?.GetType().FullName ?? string.Empty,
                ExceptionMessage = RedactSensitiveText(exception?.Message ?? string.Empty),
                ExceptionStackTrace = RedactSensitiveText(exception?.ToString() ?? string.Empty),
                StateJson = SerializeState(state),
                ScopesJson = SerializeState(scopes)
            });
        }

        private static string SerializeState(object? state)
        {
            try
            {
                return RedactSensitiveText(JsonSerializer.Serialize(NormalizeState(state)));
            }
            catch
            {
                return RedactSensitiveText(state?.ToString() ?? string.Empty);
            }
        }

        private static string RedactSensitiveText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var redacted = Regex.Replace(
                value,
                @"(?i)\b(password|pwd|api[_-]?key|token|secret)\s*=\s*([^;\s,}]+)",
                "$1=***");

            return Regex.Replace(
                redacted,
                @"(?i)(""(?:password|pwd|api[_-]?key|token|secret)""\s*:\s*"")([^""\\]*(?:\\.[^""\\]*)*)("")",
                "$1***$3");
        }

        private static object? NormalizeState(object? state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                return properties.ToDictionary(item => item.Key, item => item.Value?.ToString() ?? string.Empty);
            }

            if (state is IEnumerable<object?> values && state is not string)
            {
                return values.Select(value => value?.ToString() ?? string.Empty).ToArray();
            }

            return state?.ToString();
        }
    }

    private sealed class ApplicationRunLogEntry
    {
        public DateTime CreatedAt { get; set; }
        public string Level { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int EventId { get; set; }
        public string EventName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public string ExceptionMessage { get; set; } = string.Empty;
        public string ExceptionStackTrace { get; set; } = string.Empty;
        public string StateJson { get; set; } = string.Empty;
        public string ScopesJson { get; set; } = string.Empty;
    }
}