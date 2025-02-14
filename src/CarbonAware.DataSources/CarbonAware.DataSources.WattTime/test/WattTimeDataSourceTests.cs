﻿using CarbonAware.Exceptions;
using CarbonAware.Interfaces;
using CarbonAware.Model;
using CarbonAware.Tools.WattTimeClient;
using CarbonAware.Tools.WattTimeClient.Model;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace CarbonAware.DataSources.WattTime.Tests;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
[TestFixture]
public class WattTimeDataSourceTests
{
    private Mock<ILogger<WattTimeDataSource>> Logger { get; set; }

    private Mock<IWattTimeClient> WattTimeClient { get; set; }

    private WattTimeDataSource DataSource { get; set; }

    private Mock<ILocationSource> LocationSource { get; set; }

    // Magic floating point tolerance to allow for minuscule differences in floating point arithmetic.
    private const double FLOATING_POINT_TOLERANCE = 0.00000001;

    // Conversion factors for asserting proper unit conversions
    private const double GRAMS_TO_POUNDS = 0.00220462262185;
    private const double KWH_TO_MWH = 0.001;


    [SetUp]
    public void Setup()
    {
        this.Logger = new Mock<ILogger<WattTimeDataSource>>();
        this.WattTimeClient = new Mock<IWattTimeClient>();
        this.LocationSource = new Mock<ILocationSource>();

        this.DataSource = new WattTimeDataSource(this.Logger.Object, this.WattTimeClient.Object, this.LocationSource.Object);
    }

    [Test]
    [DefaultFloatingPointTolerance(FLOATING_POINT_TOLERANCE)]
    public async Task GetCarbonIntensity_ReturnsResultsWhenRecordsFound()
    {
        var location = new Location() { RegionName = "eastus", LocationType = LocationType.CloudProvider, CloudProvider = CloudProvider.Azure };
        var balancingAuthority = new BalancingAuthority() { Abbreviation = "BA" };
        var startDate = new DateTimeOffset(2022, 4, 18, 12, 32, 42, TimeSpan.FromHours(-6));
        var endDate = new DateTimeOffset(2022, 4, 18, 12, 33, 42, TimeSpan.FromHours(-6));
        var lbsPerMwhEmissions = 10;
        var gPerKwhEmissions = this.DataSource.ConvertMoerToGramsPerKilowattHour(lbsPerMwhEmissions);

        var emissionData = new List<GridEmissionDataPoint>()
        {
            new GridEmissionDataPoint()
            {
                BalancingAuthorityAbbreviation = balancingAuthority.Abbreviation,
                PointTime = startDate,
                Value = lbsPerMwhEmissions,
            }
        };

        this.WattTimeClient.Setup(w => w.GetDataAsync(
            balancingAuthority,
            It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>())
        ).ReturnsAsync(() => emissionData);

        SetupBalancingAuthority(balancingAuthority, location);
        var result = await this.DataSource.GetCarbonIntensityAsync(new List<Location>() { location }, startDate, endDate);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Count());

        var first = result.First();
        Assert.IsNotNull(first);
        Assert.AreEqual(gPerKwhEmissions, first.Rating);
        Assert.AreEqual(balancingAuthority.Abbreviation, first.Location);
        Assert.AreEqual(startDate, first.Time);

        this.LocationSource.Verify(r => r.ToGeopositionLocationAsync(location));
    }

    [Test]
    public async Task GetCarbonIntensity_ReturnsEmptyListWhenNoRecordsFound()
    {
        var location = new Location() { RegionName = "eastus", LocationType = LocationType.CloudProvider, CloudProvider = CloudProvider.Azure };
        var balancingAuthority = new BalancingAuthority() { Abbreviation = "BA" };
        var startDate = new DateTimeOffset(2022, 4, 18, 12, 32, 42, TimeSpan.FromHours(-6));
        var endDate = new DateTimeOffset(2022, 4, 18, 12, 33, 42, TimeSpan.FromHours(-6));

        this.WattTimeClient.Setup(w => w.GetDataAsync(
            balancingAuthority,
            startDate,
            endDate)
        ).ReturnsAsync(() => new List<GridEmissionDataPoint>());

        SetupBalancingAuthority(balancingAuthority, location);

        var result = await this.DataSource.GetCarbonIntensityAsync(new List<Location>() { location }, startDate, endDate);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Count());
    }

    [Test]
    public void GetCarbonIntensity_ThrowsWhenRegionNotFound()
    {
        var location = new Location() { RegionName = "eastus", LocationType = LocationType.CloudProvider, CloudProvider = CloudProvider.Azure };
        var startDate = new DateTimeOffset(2022, 4, 18, 12, 32, 42, TimeSpan.FromHours(-6));
        var endDate = new DateTimeOffset(2022, 4, 18, 12, 33, 42, TimeSpan.FromHours(-6));

        this.LocationSource.Setup(l => l.ToGeopositionLocationAsync(location)).Throws<LocationConversionException>();

        Assert.ThrowsAsync<LocationConversionException>(async () => await this.DataSource.GetCarbonIntensityAsync(new List<Location>() { location }, startDate, endDate));
    }

    [Test]
    public async Task GetCurrentCarbonIntensityForecastAsync_ReturnsResultsWhenRecordsFound()
    {
        // Arrange
        var location = new Location() { RegionName = "eastus", LocationType = LocationType.CloudProvider, CloudProvider = CloudProvider.Azure };
        var balancingAuthority = new BalancingAuthority() { Abbreviation = "BA" };
        var generatedAt = new DateTimeOffset(2022, 4, 18, 12, 30, 00, TimeSpan.FromHours(-6));
        var startDate = new DateTimeOffset(2022, 4, 18, 12, 32, 42, TimeSpan.FromHours(-6));
        var endDate = new DateTimeOffset(2022, 4, 18, 12, 33, 42, TimeSpan.FromHours(-6));
        var lbsPerMwhEmissions = 10;
        var gPerKwhEmissions = this.DataSource.ConvertMoerToGramsPerKilowattHour(lbsPerMwhEmissions);
        var expectedDuration = TimeSpan.FromMinutes(5);

        var emissionData = new List<GridEmissionDataPoint>()
        {
            new GridEmissionDataPoint()
            {
                BalancingAuthorityAbbreviation = balancingAuthority.Abbreviation,
                PointTime = startDate,
                Value = lbsPerMwhEmissions,
            },
            new GridEmissionDataPoint()
            {
                BalancingAuthorityAbbreviation = balancingAuthority.Abbreviation,
                PointTime = startDate + expectedDuration,
                Value = lbsPerMwhEmissions,
            },
        };
        var forecast = new Forecast(){
            GeneratedAt = generatedAt,
            ForecastData = emissionData
        };

        this.WattTimeClient.Setup(w => w.GetCurrentForecastAsync(balancingAuthority)
            ).ReturnsAsync(() => forecast);

        SetupBalancingAuthority(balancingAuthority, location);

        // Act
        var result = await this.DataSource.GetCurrentCarbonIntensityForecastAsync(location);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(generatedAt, result.GeneratedAt);
        Assert.AreEqual(location, result.Location);

        var firstDataPoint = result.ForecastData.First();
        var lastDataPoint = result.ForecastData.Last();
        Assert.IsNotNull(firstDataPoint);
        Assert.AreEqual(gPerKwhEmissions, firstDataPoint.Rating);
        Assert.AreEqual(balancingAuthority.Abbreviation, firstDataPoint.Location);
        Assert.AreEqual(startDate, firstDataPoint.Time);
        Assert.AreEqual(expectedDuration, firstDataPoint.Duration);

        Assert.IsNotNull(lastDataPoint);
        Assert.AreEqual(gPerKwhEmissions, lastDataPoint.Rating);
        Assert.AreEqual(balancingAuthority.Abbreviation, lastDataPoint.Location);
        Assert.AreEqual(startDate + expectedDuration, lastDataPoint.Time);
        Assert.AreEqual(expectedDuration, lastDataPoint.Duration);

        this.LocationSource.Verify(r => r.ToGeopositionLocationAsync(location));
    }
    
    [Test]
    public void GetCurrentCarbonIntensityForecastAsync_ThrowsWhenRegionNotFound()
    {
        var location = new Location() { RegionName = "eastus", LocationType = LocationType.CloudProvider, CloudProvider = CloudProvider.Azure };

        this.LocationSource.Setup(l => l.ToGeopositionLocationAsync(location)).Throws<LocationConversionException>();

        Assert.ThrowsAsync<LocationConversionException>(async () => await this.DataSource.GetCurrentCarbonIntensityForecastAsync(location));
    }

    [Test]
    public void GetCurrentCarbonIntensityForecastAsync_ThrowsWhenNoForecastFound()
    {
        var location = new Location() { RegionName = "eastus", LocationType = LocationType.CloudProvider, CloudProvider = CloudProvider.Azure };
        var balancingAuthority = new BalancingAuthority() { Abbreviation = "BA" };
        this.WattTimeClient.Setup(w => w.GetCurrentForecastAsync(balancingAuthority)).ThrowsAsync(new WattTimeClientException("No forecast"));

        SetupBalancingAuthority(balancingAuthority, location);

        Assert.ThrowsAsync<WattTimeClientException>(async () => await this.DataSource.GetCurrentCarbonIntensityForecastAsync(location));
    }

    [TestCase(0, TestName="No datapoints")]
    [TestCase(1, TestName="1 datapoint")]
    public void GetCurrentCarbonIntensityForecastAsync_ThrowsWhenTooFewDatapointsReturned(int numDataPoints)
    {
        // Arrange
        var location = new Location() { RegionName = "eastus", LocationType = LocationType.CloudProvider, CloudProvider = CloudProvider.Azure };
        var balancingAuthority = new BalancingAuthority() { Abbreviation = "BA" };
        var emissionData = new List<GridEmissionDataPoint>();
        for(var i=0; i<numDataPoints; i++)
        {
            emissionData.Add(new GridEmissionDataPoint());
        }
        var forecast = new Forecast(){
            GeneratedAt = DateTimeOffset.Now,
            ForecastData = emissionData
        };

        this.WattTimeClient.Setup(w => w.GetCurrentForecastAsync(balancingAuthority)
            ).ReturnsAsync(() => forecast);

        SetupBalancingAuthority(balancingAuthority, location);

        Assert.ThrowsAsync<WattTimeClientException>(async () => await this.DataSource.GetCurrentCarbonIntensityForecastAsync(location));
    }

    [DatapointSource]
    public float[] moerValues = new float[] { 0.0F, 10.0F, 100.0F, 1000.0F, 596.1367F};

    [Theory]
    public void GetCarbonIntensity_ConvertsMoerToGramsPerKwh(float lbsPerMwhEmissions)
    {
        Assume.That(lbsPerMwhEmissions >= 0.0);

        var result = this.DataSource.ConvertMoerToGramsPerKilowattHour(lbsPerMwhEmissions);

        Assert.That(result >= 0.0);
        Assert.That(result * GRAMS_TO_POUNDS / KWH_TO_MWH, Is.EqualTo(lbsPerMwhEmissions).Within(FLOATING_POINT_TOLERANCE));
    }

    private void SetupBalancingAuthority(BalancingAuthority balancingAuthority, Location location)
    {
        this.LocationSource.Setup(r => r.ToGeopositionLocationAsync(location)).Returns(Task.FromResult(location));
        var latitude = location.Latitude.ToString() ?? throw new ArgumentNullException(String.Format("Could not find location"));
        var longitude = location.Longitude.ToString() ?? throw new ArgumentNullException(String.Format("Could not find location"));

        this.WattTimeClient.Setup(w => w.GetBalancingAuthorityAsync(latitude, longitude)
        ).ReturnsAsync(() => balancingAuthority);

    }
}
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
