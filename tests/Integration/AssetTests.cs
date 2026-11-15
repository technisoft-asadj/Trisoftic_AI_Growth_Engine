using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class AssetTests(TestHost host)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private static async Task<Guid> ProductAsync(HttpClient admin)
    {
        var cats = await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", Json);
        var catId = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Android Game").GetProperty("id").GetGuid();
        var r = await admin.PostAsJsonAsync("/api/v1/products", new { name = "Ludo " + Guid.NewGuid().ToString("N")[..6], categoryId = catId });
        return (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
    }

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string? kind = "Screenshot", string? source = "Own", string? rights = "Unrestricted", string? extra = null)
    {
        var f = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        f.Add(file, "file", fileName);
        if (kind is not null) f.Add(new StringContent(kind), "kind");
        if (source is not null) f.Add(new StringContent(source), "source");
        if (rights is not null) f.Add(new StringContent(rights), "rights");
        if (extra == "synthetic") f.Add(new StringContent("true"), "isSynthetic");
        return f;
    }

    [Fact]
    public async Task Upload_download_roundtrip_with_security_headers_and_audit()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);

        var up = await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(Png, "shot one!.png"));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var a = await up.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("shot one_.png", a.GetProperty("name").GetString());       // '!' sanitized, spaces kept
        Assert.Equal("image/png", a.GetProperty("contentType").GetString());
        Assert.Equal(1, a.GetProperty("version").GetInt32());
        Assert.False(a.GetProperty("isSynthetic").GetBoolean());

        var dl = await admin.GetAsync($"/api/v1/assets/{a.GetProperty("id").GetGuid()}/content");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal(Png, await dl.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", dl.Headers.GetValues("X-Content-Type-Options").Single());

        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/governance/audit?productId={pid}", Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "asset.uploaded");
    }

    [Fact]
    public async Task Disguised_or_disallowed_files_are_rejected()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var exe = "MZ\u0090\0\u0003\0\0\0"u8.ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(exe, "evil.png"))).StatusCode);        // wrong magic
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(exe, "evil.exe"))).StatusCode);        // type not allowed
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form("<svg onload=alert(1)>"u8.ToArray(), "x.svg"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form([], "empty.png"))).StatusCode);
    }

    [Fact]
    public async Task Source_and_rights_metadata_are_mandatory_and_ai_assets_are_always_synthetic()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(Png, "a.png", rights: null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(Png, "a.png", source: null))).StatusCode);

        var ai = await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(Png, "gen.png", source: "AiGenerated", rights: "InternalOnly"));
        Assert.True((await ai.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("isSynthetic").GetBoolean());
    }

    [Fact]
    public async Task New_versions_keep_history_and_list_shows_latest()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var v1 = await (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(Png, "logo.png", kind: "Logo"))).Content.ReadFromJsonAsync<JsonElement>(Json);
        var png2 = Png.Concat(new byte[] { 99 }).ToArray();
        var v2 = await admin.PostAsync($"/api/v1/assets/{v1.GetProperty("id").GetGuid()}/versions", Form(png2, "logo.png", kind: "Logo"));
        Assert.Equal(HttpStatusCode.Created, v2.StatusCode);
        var v2j = await v2.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(2, v2j.GetProperty("version").GetInt32());
        Assert.Equal(v1.GetProperty("groupId").GetGuid(), v2j.GetProperty("groupId").GetGuid());
        Assert.NotEqual(v1.GetProperty("sha256").GetString(), v2j.GetProperty("sha256").GetString());

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/assets", Json);
        Assert.Equal(2, list.EnumerateArray().Single().GetProperty("version").GetInt32());
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/assets/{v1.GetProperty("id").GetGuid()}", Json);
        Assert.Equal(2, detail.GetProperty("versions").GetArrayLength());
    }

    [Fact]
    public async Task Product_scope_and_permissions_protect_assets()
    {
        var admin = await host.LoginAdminAsync();
        var a = await ProductAsync(admin); var b = await ProductAsync(admin);
        var asset = await (await admin.PostAsync($"/api/v1/products/{b}/assets", Form(Png, "secret.png"))).Content.ReadFromJsonAsync<JsonElement>(Json);
        var id = asset.GetProperty("id").GetGuid();

        var email = $"pm-{Guid.NewGuid():N}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email, displayName = "pm", password = "Some-Strong-Pass-123", roles = new[] { "Product Manager" }, allProducts = false, productIds = new[] { a } })).EnsureSuccessStatusCode();
        var pm = await host.LoginAsync(email, "Some-Strong-Pass-123");

        Assert.Equal(HttpStatusCode.NotFound, (await pm.GetAsync($"/api/v1/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pm.GetAsync($"/api/v1/assets/{id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pm.GetAsync($"/api/v1/products/{b}/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pm.PostAsync($"/api/v1/products/{b}/assets", Form(Png, "x.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await pm.PostAsync($"/api/v1/products/{a}/assets", Form(Png, "ok.png"))).StatusCode);

        var viewerEmail = $"v-{Guid.NewGuid():N}@test.local";
        (await admin.PostAsJsonAsync("/api/v1/users", new { email = viewerEmail, displayName = "v", password = "Some-Strong-Pass-123", roles = new[] { "Read-only Viewer" }, allProducts = true })).EnsureSuccessStatusCode();
        var viewer = await host.LoginAsync(viewerEmail, "Some-Strong-Pass-123");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/assets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.PostAsync($"/api/v1/products/{b}/assets", Form(Png, "x.png"))).StatusCode);
    }

    [Fact]
    public async Task Retiring_hides_the_asset_from_the_default_list()
    {
        var admin = await host.LoginAdminAsync();
        var pid = await ProductAsync(admin);
        var asset = await (await admin.PostAsync($"/api/v1/products/{pid}/assets", Form(Png, "old.png"))).Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/assets/{asset.GetProperty("id").GetGuid()}/retire/true", null)).StatusCode);
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/assets", Json)).GetArrayLength());
        Assert.Equal(1, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/products/{pid}/assets?includeRetired=true", Json)).GetArrayLength());
    }
}
