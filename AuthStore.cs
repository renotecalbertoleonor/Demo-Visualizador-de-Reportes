using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

// Authentication and administration of visualizer users. No INSERT/UPDATE in integrator tables.
public sealed class AuthStore(IConfiguration configuration)
{
    const string CookieName = "RRV_SESSION";
    string Connection => configuration.GetConnectionString("SquareOne") ?? throw new InvalidOperationException("Missing SquareOne connection");
    async Task<SqlConnection> Open(CancellationToken ct) { var c = new SqlConnection(Connection); await c.OpenAsync(ct); return c; }
    static SqlCommand Cmd(SqlConnection c, string sql, params (string name, object? value)[] parameters)
    { var cmd = new SqlCommand(sql,c); foreach(var p in parameters)cmd.Parameters.AddWithValue(p.name,p.value??DBNull.Value);return cmd; }
    static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string HashPassword(string password)
    { var salt=RandomNumberGenerator.GetBytes(16); var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,310000,HashAlgorithmName.SHA256,32);return $"PBKDF2$310000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}"; }
    static bool VerifyPassword(string password,string stored)
    { try { var p=stored.Split('$');if(p.Length!=4||p[0]!="PBKDF2")return false;var salt=Convert.FromBase64String(p[2]);var expected=Convert.FromBase64String(p[3]);var actual=Rfc2898DeriveBytes.Pbkdf2(password,salt,int.Parse(p[1]),HashAlgorithmName.SHA256,expected.Length);return CryptographicOperations.FixedTimeEquals(actual,expected); }catch{return false;} }
    public async Task Bootstrap(CancellationToken ct)
    {
        var pass=Environment.GetEnvironmentVariable("RRV_BOOTSTRAP_PASSWORD");
        if(string.IsNullOrWhiteSpace(pass))return;
        if(pass.Length<12)throw new InvalidOperationException("RRV_BOOTSTRAP_PASSWORD must contain at least 12 characters");
        await using var c=await Open(ct);
        await using var cmd=Cmd(c,"IF NOT EXISTS(SELECT 1 FROM rrv.Users) INSERT INTO rrv.Users(FullName,Username,Email,PasswordHash,RoleId) SELECT N'Administrador',N'admin',N'admin@renotec.local',@hash,Id FROM rrv.Roles WHERE Name=N'Administrador'",("@hash",HashPassword(pass)));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<object?> Login(HttpContext ctx,LoginRequest input,CancellationToken ct)
    {
        await using var c=await Open(ct);
        await using var cmd=Cmd(c,"SELECT u.Id,u.FullName,u.PasswordHash FROM rrv.Users u WHERE u.Username=@username AND u.IsActive=1 AND EXISTS(SELECT 1 FROM rrv.Roles r WHERE r.Id=u.RoleId AND r.IsActive=1)",("@username",input.Username?.Trim()));
        int id=0;string name="",hash="";
        await using(var rd=await cmd.ExecuteReaderAsync(ct))if(await rd.ReadAsync(ct)){id=rd.GetInt32(0);name=rd.GetString(1);hash=rd.GetString(2);}
        if(id==0||!VerifyPassword(input.Password??"",hash))return null;
        var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using(var save=Cmd(c,"INSERT INTO rrv.UserSessions(UserId,TokenHash,ExpiresAt) VALUES(@id,@token,DATEADD(hour,8,SYSUTCDATETIME()));UPDATE rrv.Users SET LastLoginAt=SYSUTCDATETIME() WHERE Id=@id",("@id",id),("@token",HashToken(token))))await save.ExecuteNonQueryAsync(ct);
        ctx.Response.Cookies.Append(CookieName,token,new CookieOptions{HttpOnly=true,Secure=ctx.Request.IsHttps,SameSite=SameSiteMode.Strict,Expires=DateTimeOffset.UtcNow.AddHours(8),IsEssential=true,Path="/"});
        return new {name};
    }
    public async Task<UserIdentity?> GetIdentity(HttpContext ctx,CancellationToken ct)
    {
        if(!ctx.Request.Cookies.TryGetValue(CookieName,out var token)||string.IsNullOrWhiteSpace(token))return null;
        await using var c=await Open(ct);
        await using var cmd=Cmd(c,"SELECT u.Id,u.FullName,u.Username,r.Name,p.Code FROM rrv.UserSessions s JOIN rrv.Users u ON u.Id=s.UserId JOIN rrv.Roles r ON r.Id=u.RoleId LEFT JOIN rrv.RolePermissions rp ON rp.RoleId=r.Id LEFT JOIN rrv.Permissions p ON p.Id=rp.PermissionId WHERE s.TokenHash=@token AND s.RevokedAt IS NULL AND s.ExpiresAt>SYSUTCDATETIME() AND u.IsActive=1 AND r.IsActive=1",("@token",HashToken(token)));
        UserIdentity? user=null;var permissions=new List<string>();
        await using(var rd=await cmd.ExecuteReaderAsync(ct))while(await rd.ReadAsync(ct)){user??=new(rd.GetInt32(0),rd.GetString(1),rd.GetString(2),rd.GetString(3),permissions);if(!rd.IsDBNull(4))permissions.Add(rd.GetString(4));}
        return user;
    }
    public async Task Logout(HttpContext ctx,CancellationToken ct)
    {
        if(ctx.Request.Cookies.TryGetValue(CookieName,out var token)){await using var c=await Open(ct);await using var cmd=Cmd(c,"UPDATE rrv.UserSessions SET RevokedAt=SYSUTCDATETIME() WHERE TokenHash=@token",("@token",HashToken(token)));await cmd.ExecuteNonQueryAsync(ct);}ctx.Response.Cookies.Delete(CookieName);
    }
    public async Task<object> ListUsers(CancellationToken ct)
    {
        await using var c=await Open(ct);var roles=new List<object>();var users=new List<object>();
        await using(var cmd=Cmd(c,"SELECT Id,Name FROM rrv.Roles WHERE IsActive=1 ORDER BY Id"))await using(var rd=await cmd.ExecuteReaderAsync(ct))while(await rd.ReadAsync(ct))roles.Add(new{id=rd.GetInt32(0),name=rd.GetString(1)});
        await using(var cmd=Cmd(c,"SELECT u.Id,u.FullName,u.Username,u.Email,u.RoleId,r.Name,u.IsActive,u.LastLoginAt FROM rrv.Users u JOIN rrv.Roles r ON r.Id=u.RoleId ORDER BY u.Id"))await using(var rd=await cmd.ExecuteReaderAsync(ct))while(await rd.ReadAsync(ct))users.Add(new{id=rd.GetInt32(0),fullName=rd.GetString(1),username=rd.GetString(2),email=rd.GetString(3),roleId=rd.GetInt32(4),role=rd.GetString(5),isActive=rd.GetBoolean(6),lastLoginAt=rd.IsDBNull(7)?null:rd.GetDateTime(7).ToString("yyyy-MM-dd HH:mm")});
        return new{users,roles};
    }
    public async Task SaveUser(UserInput input,int? id,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(input.FullName)||string.IsNullOrWhiteSpace(input.Username)||string.IsNullOrWhiteSpace(input.Email)||input.RoleId<1)throw new ArgumentException("Complete nombre, usuario, correo y rol");
        if(id is null && (input.Password?.Length??0)<12)throw new ArgumentException("Contraseña inicial: al menos 12 caracteres");
        if(!string.IsNullOrEmpty(input.Password)&&input.Password.Length<12)throw new ArgumentException("Contraseña: al menos 12 caracteres");
        await using var c=await Open(ct);
        var hash=string.IsNullOrEmpty(input.Password)?null:HashPassword(input.Password);
        var sql=id is null?"INSERT INTO rrv.Users(FullName,Username,Email,PasswordHash,RoleId,IsActive) SELECT @full,@username,@email,@hash,Id,@active FROM rrv.Roles WHERE Id=@role AND IsActive=1":"UPDATE rrv.Users SET FullName=@full,Email=@email,RoleId=@role,IsActive=@active,UpdatedAt=SYSUTCDATETIME(),PasswordHash=COALESCE(@hash,PasswordHash) WHERE Id=@id AND EXISTS(SELECT 1 FROM rrv.Roles WHERE Id=@role AND IsActive=1)";
        await using var cmd=Cmd(c,sql,("@full",input.FullName.Trim()),("@username",input.Username.Trim()),("@email",input.Email.Trim()),("@hash",hash),("@role",input.RoleId),("@active",input.IsActive),("@id",id));
        if(await cmd.ExecuteNonQueryAsync(ct)==0)throw new ArgumentException("No se encontró el usuario o rol seleccionado");
        if(id.HasValue && (!input.IsActive||hash is not null)){await using var revoke=Cmd(c,"UPDATE rrv.UserSessions SET RevokedAt=SYSUTCDATETIME() WHERE UserId=@id AND RevokedAt IS NULL",("@id",id.Value));await revoke.ExecuteNonQueryAsync(ct);}
    }
}
public record LoginRequest(string? Username,string? Password);
public record UserInput(string FullName,string Username,string Email,string? Password,int RoleId,bool IsActive);
public record UserIdentity(int Id,string FullName,string Username,string Role,List<string> Permissions);
