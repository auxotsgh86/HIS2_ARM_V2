using BCrypt.Net;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;
using System.Text.Json;
using System.Threading.Channels;

partial class Program
{
    private const int ChannelCapacity = 500;
    private const int BatchSize = 500;

    static async Task Main(string[] args)
    {
        var appSettingsPath =
            Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        var appSettingsJson =
            await File.ReadAllTextAsync(appSettingsPath);

        using var appSettingsDoc =
            JsonDocument.Parse(appSettingsJson);

        var connectionString =
            appSettingsDoc.RootElement
                .GetProperty("ConnectionStrings")
                .GetProperty("Oracle")
                .GetString()
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Oracle 未設定");

        var filter =
            ParseArgs(args);

        if (filter.UserId is not null)
        {
            Console.WriteLine(
                $"單筆模式：僅轉換 USERID = {filter.UserId}");
        }

        var services = new ServiceCollection();

        services.AddSingleton(connectionString);

        services.AddSingleton<UserPasswordService>();

        var provider =
            services.BuildServiceProvider();

        var dbService =
            provider.GetRequiredService<UserPasswordService>();

        var userChannel =
            Channel.CreateBounded<UserPassword>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait
                });

        var hashChannel =
            Channel.CreateBounded<UserPasswordHash>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait
                });

        Console.WriteLine("開始轉換密碼...");

        var producerTask =
            ProduceUsersAsync(
                dbService,
                userChannel.Writer,
                filter);

        int workerCount =
            Math.Max(
                Environment.ProcessorCount / 2,
                4);

        Console.WriteLine(
            $"BCrypt Workers = {workerCount}");

        var hashTasks =
            Enumerable.Range(0, workerCount)
                .Select(_ =>
                    HashPasswordsAsync(
                        userChannel.Reader,
                        hashChannel.Writer))
                .ToArray();

        var updateTask =
            UpdateHashesAsync(
                dbService,
                hashChannel.Reader);

        await producerTask;

        await Task.WhenAll(hashTasks);

        hashChannel.Writer.Complete();

        long totalUpdated =
            await updateTask;

        if (filter.UserId is not null
            && totalUpdated == 0)
        {
            Console.Error.WriteLine(
                $"警告：查無符合條件的資料（USERID = {filter.UserId}），未更新任何筆數，請確認 USERID 是否正確。");

            Environment.ExitCode = 1;

            return;
        }

        Console.WriteLine("所有密碼已更新完成");
    }

    /// <summary>
    /// 解析命令列參數，支援：
    ///   --userid=XXX
    ///   --userid XXX
    ///   -u XXX
    /// 未帶任何 userid 參數時，回傳 UserId = null（代表全量轉換）。
    /// </summary>
    private static UserFilter ParseArgs(string[] args)
    {
        string? userId = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg.StartsWith(
                    "--userid=",
                    StringComparison.OrdinalIgnoreCase))
            {
                userId = arg["--userid=".Length..];
            }
            else if (
                (arg.Equals(
                    "--userid",
                    StringComparison.OrdinalIgnoreCase)
                 || arg.Equals(
                    "-u",
                    StringComparison.OrdinalIgnoreCase))
                && i + 1 < args.Length)
            {
                userId = args[i + 1];
                i++;
            }
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = null;
        }

        return new UserFilter(userId);
    }

    private static async Task ProduceUsersAsync(
        UserPasswordService service,
        ChannelWriter<UserPassword> writer,
        UserFilter filter)
    {
        try
        {
            await foreach (var user in service.GetSysUserPasswordsAsync(filter))
            {
                await writer.WriteAsync(user);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    private static async Task HashPasswordsAsync(
        ChannelReader<UserPassword> reader,
        ChannelWriter<UserPasswordHash> writer)
    {
        await foreach (var user in reader.ReadAllAsync())
        {
            string hash =
                BCrypt.Net.BCrypt.HashPassword(
                    user.Password);

            await writer.WriteAsync(
                new UserPasswordHash(
                    user.UserId,
                    hash));
        }
    }

    private static async Task<long> UpdateHashesAsync(
        UserPasswordService service,
        ChannelReader<UserPasswordHash> reader)
    {
        var batch = new List<UserPasswordHash>(BatchSize);

        long totalUpdated = 0;

        await foreach (var item in reader.ReadAllAsync())
        {
            batch.Add(item);

            if (batch.Count >= BatchSize)
            {
                await service.UpdateArmUserPasswordHashesAsync(batch);

                totalUpdated += batch.Count;

                Console.WriteLine(
                    $"累計已更新 {totalUpdated:N0} 筆");

                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await service.UpdateArmUserPasswordHashesAsync(batch);

            totalUpdated += batch.Count;

            Console.WriteLine(
                $"累計已更新 {totalUpdated:N0} 筆");
        }

        Console.WriteLine(
            $"更新完成，共 {totalUpdated:N0} 筆");

        return totalUpdated;
    }
}

/// <summary>
/// 轉換條件篩選器。UserId 為 null 時代表全量轉換；
/// 有值時僅轉換該筆 USERID。
/// </summary>
public sealed record UserFilter(
    string? UserId);

public sealed record UserPassword(
    string UserId,
    string Password);

public sealed record UserPasswordHash(
    string UserId,
    string PasswordHash);

public class UserPasswordService
{
    private readonly string _connectionString;

    public UserPasswordService(
        string connectionString)
    {
        _connectionString = connectionString;
    }

    public async IAsyncEnumerable<UserPassword>
        GetSysUserPasswordsAsync(UserFilter filter)
    {
        await using var conn =
            new OracleConnection(_connectionString);

        await conn.OpenAsync();

        string sql =
            filter.UserId is null
                ? @"SELECT USERID,
                           PASSWORDS
                    FROM HIS2USER2.SYSUSERAUTHOR"
                : @"SELECT USERID,
                           PASSWORDS
                    FROM HIS2USER2.SYSUSERAUTHOR
                   WHERE USERID = :USERID";

        await using var cmd =
            new OracleCommand(sql, conn);

        cmd.BindByName = true;

        if (filter.UserId is not null)
        {
            cmd.Parameters.Add(
                "USERID",
                OracleDbType.Varchar2,
                filter.UserId,
                System.Data.ParameterDirection.Input);
        }

        await using var reader =
            await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string userId =
                reader.IsDBNull(0)
                    ? string.Empty
                    : reader.GetString(0);

            string password =
                reader.IsDBNull(1)
                    ? string.Empty
                    : reader.GetString(1);

            yield return new UserPassword(
                userId,
                password);
        }
    }

    public async Task UpdateArmUserPasswordHashesAsync(
        IReadOnlyList<UserPasswordHash> hashes)
    {
        if (hashes.Count == 0)
        {
            return;
        }

        await using var conn =
            new OracleConnection(_connectionString);

        await conn.OpenAsync();

        await using var tx =
            conn.BeginTransaction();

        try
        {
            string[] userIds =
                hashes
                    .Select(x => x.UserId)
                    .ToArray();

            string[] passwordHashes =
                hashes
                    .Select(x => x.PasswordHash)
                    .ToArray();

            await using var cmd =
                conn.CreateCommand();

            cmd.Transaction = tx;

            cmd.BindByName = true;

            cmd.CommandText =
                @"UPDATE HIS2USER2.ARM_USERS
                     SET PASSWORD_HASH = :PASSWORD_HASH
                   WHERE USER_ID = :USER_ID";

            cmd.ArrayBindCount =
                hashes.Count;

            cmd.Parameters.Add(
                "PASSWORD_HASH",
                OracleDbType.Varchar2,
                passwordHashes,
                System.Data.ParameterDirection.Input);

            cmd.Parameters.Add(
                "USER_ID",
                OracleDbType.Varchar2,
                userIds,
                System.Data.ParameterDirection.Input);

            await cmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}