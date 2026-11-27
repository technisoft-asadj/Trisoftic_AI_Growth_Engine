using System.Net.Http.Json;
using System.Text.Json;

namespace TriSoftic.GrowthEngine.Tests.Integration;

[Collection("host")]
public sealed class ProductCategoryTests(TestHost host)
{
    [Fact]
    public async Task The_standard_product_categories_include_a_software_development_company_as_business_software()
    {
        var admin = await host.LoginAdminAsync();
        var cats = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/product-categories", new JsonSerializerOptions(JsonSerializerDefaults.Web))).EnumerateArray().ToList();
        var c = Assert.Single(cats, x => x.GetProperty("name").GetString() == "Software development company");
        Assert.Equal("BusinessSoftware", c.GetProperty("kind").ToString());
    }
}
