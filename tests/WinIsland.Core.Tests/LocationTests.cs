using WinIsland.Core.Personal;

namespace WinIsland.Core.Tests;

public sealed class LocationTests
{
    [Fact]
    public void Geocode_names_the_city_and_country()
    {
        var found = OpenMeteo.ParseGeocode("""{"results":[{"name":"Casablanca","country":"Morocco","latitude":33.57,"longitude":-7.59}]}""");

        Assert.Equal((33.57, -7.59, "Casablanca, Morocco"), found);
    }

    [Theory]
    [InlineData("Paris", "France", "Paris, France")]
    [InlineData("Singapore", "Singapore", "Singapore")]
    [InlineData("", "Chile", "Chile")]
    [InlineData(null, null, "")]
    public void Place_joins_the_known_parts(string? city, string? country, string expected) =>
        Assert.Equal(expected, OpenMeteo.Place(city, country));

    [Fact]
    public void Reverse_geocode_prefers_city_then_locality()
    {
        Assert.Equal("Rabat, Morocco", OpenMeteo.ParseReverseGeocode("""{"city":"Rabat","locality":"Agdal","countryName":"Morocco"}"""));
        Assert.Equal("Agdal, Morocco", OpenMeteo.ParseReverseGeocode("""{"city":"","locality":"Agdal","countryName":"Morocco"}"""));
    }

    [Fact]
    public void Ip_location_accepts_string_coordinates()
    {
        var ip = OpenMeteo.ParseIpLocation("""{"latitude":"33.5883","longitude":"-7.6114","city":"Casablanca","country":"Morocco"}""");

        Assert.Equal((33.5883, -7.6114, "Casablanca, Morocco"), ip);
        Assert.Null(OpenMeteo.ParseIpLocation("""{"city":"x"}"""));
    }
}
