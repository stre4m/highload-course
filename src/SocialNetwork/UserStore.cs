using Npgsql;

public sealed class UserStore(NpgsqlDataSource dataSource)
{
    public async Task InsertAsync(Profile profile, string passwordHash, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
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
        await using var command = dataSource.CreateCommand("SELECT password_hash FROM users WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync(ct);
    }

    public async Task<Profile?> GetAsync(Guid id, CancellationToken ct)
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
