using Npgsql;

public sealed class UserStore(NpgsqlDataSource primary, NpgsqlDataSource publicReads)
{
    public async Task InsertAsync(Profile profile, string passwordHash, CancellationToken ct)
    {
        await using var command = primary.CreateCommand("""
            INSERT INTO users (id, first_name, second_name, birthdate, gender, biography, city, password_hash)
            VALUES (@id, @first_name, @second_name, @birthdate, @gender, @biography, @city, @password_hash)
            """);
        command.Parameters.AddWithValue("id", profile.Id);
        command.Parameters.AddWithValue("first_name", profile.FirstName);
        command.Parameters.AddWithValue("second_name", profile.SecondName);
        command.Parameters.AddWithValue("birthdate", profile.Birthdate);
        command.Parameters.AddWithValue("gender", profile.Gender);
        command.Parameters.AddWithValue("biography", profile.Biography);
        command.Parameters.AddWithValue("city", profile.City);
        command.Parameters.AddWithValue("password_hash", passwordHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetPasswordHashAsync(Guid id, CancellationToken ct)
    {
        await using var command = primary.CreateCommand("SELECT password_hash FROM users WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    public async Task<IReadOnlyList<UserSearchResult>> SearchAsync(string firstName, string lastName, CancellationToken ct)
    {
        await using var command = publicReads.CreateCommand("""
            SELECT id, first_name, second_name, birthdate, biography, city
            FROM users
            WHERE first_name LIKE @first_name ESCAPE '!'
              AND second_name LIKE @last_name ESCAPE '!'
            ORDER BY id
            """);
        command.Parameters.AddWithValue("first_name", ToPrefixPattern(firstName));
        command.Parameters.AddWithValue("last_name", ToPrefixPattern(lastName));

        var results = new List<UserSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new UserSearchResult(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateOnly>(3), reader.GetString(4), reader.GetString(5)));
        }

        return results;
    }

    private static string ToPrefixPattern(string prefix) => prefix
        .Replace("!", "!!")
        .Replace("%", "!%")
        .Replace("_", "!_") + "%";

    public Task<Profile?> GetAsync(Guid id, CancellationToken ct) =>
        GetProfileAsync(publicReads, id, ct);

    public Task<Profile?> GetCurrentAsync(Guid id, CancellationToken ct) =>
        GetProfileAsync(primary, id, ct);

    private static async Task<Profile?> GetProfileAsync(NpgsqlDataSource dataSource, Guid id, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, first_name, second_name, birthdate, gender, biography, city
            FROM users WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Profile(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateOnly>(3), reader.GetString(4), reader.GetString(5), reader.GetString(6))
            : null;
    }
}
