using Microsoft.Data.SqlClient;

namespace PoolSense.Api.Data;

public interface IPoolSenseSqlConnectionFactory
{
    SqlConnection CreateConnection();
}

public sealed class PoolSenseSqlConnectionFactory : IPoolSenseSqlConnectionFactory
{
    private readonly IConfiguration _configuration;

    public PoolSenseSqlConnectionFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public SqlConnection CreateConnection()
    {
        return new SqlConnection(GetConnectionString());
    }

    private string GetConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("PoolSenseSqlServer")
            ?? _configuration.GetConnectionString("SqlServer")
            ?? _configuration.GetConnectionString("DefaultConnection")
            ?? _configuration.GetConnectionString("TicketSourceSqlServer");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("A SQL Server PoolSense persistence connection string was not found. Configure ConnectionStrings:PoolSenseSqlServer, ConnectionStrings:SqlServer, ConnectionStrings:DefaultConnection, or ConnectionStrings:TicketSourceSqlServer.");
        }

        return connectionString;
    }
}