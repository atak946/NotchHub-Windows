using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Notch.Services;

public sealed record HourForecast(DateTime Time, double Temp, int Code);

public sealed record WeatherNow(string City, double Temp, double Feels, int Code, bool IsDay,
                                double Wind, int Humidity, List<HourForecast> Hours);

/// <summary>Open-Meteo (ucretsiz, anahtar gerektirmez) ile hava durumu. Yalnizca ayarlardaki sehir adi gonderilir.</summary>
public sealed class WeatherService
{
    public event Action? Updated;
    public WeatherNow? Current { get; private set; }
    public string? Error { get; private set; }

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(15) };
    string? _geoFor;
    double _lat, _lon;
    string _cityName = "";

    public WeatherService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Notch/1.0");
        _timer.Tick += async (_, _) => await Refresh();
    }

    public void Start()
    {
        _timer.Start();
        _ = Refresh();
    }

    public async Task Refresh()
    {
        var city = AppSettings.Current.WeatherCity?.Trim() ?? "";
        if (city.Length == 0)
        {
            Current = null; Error = null; _geoFor = null;
            Updated?.Invoke();
            return;
        }
        try
        {
            if (_geoFor != city)
            {
                var g = await _http.GetStringAsync(
                    $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city)}&count=1&language=tr&format=json");
                using var gd = JsonDocument.Parse(g);
                if (!gd.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0)
                {
                    Error = $"\"{city}\" bulunamadı";
                    Current = null;
                    Updated?.Invoke();
                    return;
                }
                var r0 = res[0];
                _lat = r0.GetProperty("latitude").GetDouble();
                _lon = r0.GetProperty("longitude").GetDouble();
                _cityName = r0.GetProperty("name").GetString() ?? city;
                _geoFor = city;
            }

            var inv = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast?latitude=" + _lat.ToString(inv) + "&longitude=" + _lon.ToString(inv) +
                      "&current=temperature_2m,apparent_temperature,weather_code,is_day,wind_speed_10m,relative_humidity_2m" +
                      "&hourly=temperature_2m,weather_code&forecast_hours=12&timezone=auto";
            using var d = JsonDocument.Parse(await _http.GetStringAsync(url));
            var c = d.RootElement.GetProperty("current");
            var h = d.RootElement.GetProperty("hourly");
            var times = h.GetProperty("time");
            var temps = h.GetProperty("temperature_2m");
            var codes = h.GetProperty("weather_code");
            var hours = new List<HourForecast>();
            for (int i = 0; i < times.GetArrayLength(); i++)
                hours.Add(new HourForecast(DateTime.Parse(times[i].GetString()!, inv), temps[i].GetDouble(), codes[i].GetInt32()));

            Current = new WeatherNow(_cityName,
                c.GetProperty("temperature_2m").GetDouble(),
                c.GetProperty("apparent_temperature").GetDouble(),
                c.GetProperty("weather_code").GetInt32(),
                c.GetProperty("is_day").GetInt32() == 1,
                c.GetProperty("wind_speed_10m").GetDouble(),
                c.GetProperty("relative_humidity_2m").GetInt32(),
                hours);
            Error = null;
        }
        catch
        {
            Error = "Hava durumu alınamadı";
        }
        Updated?.Invoke();
    }

    public static string Icon(int code, bool day = true) => code switch
    {
        0 => day ? "☀" : "☾",
        1 or 2 => day ? "⛅" : "☁",
        3 => "☁",
        45 or 48 => "🌫",
        >= 51 and <= 67 => "🌧",
        >= 71 and <= 77 => "❄",
        >= 80 and <= 82 => "🌦",
        85 or 86 => "🌨",
        >= 95 => "⛈",
        _ => "☁",
    };

    public static string Describe(int code) => code switch
    {
        0 => "Açık",
        1 => "Az bulutlu",
        2 => "Parçalı bulutlu",
        3 => "Kapalı",
        45 or 48 => "Sisli",
        >= 51 and <= 57 => "Çiseleme",
        >= 61 and <= 67 => "Yağmurlu",
        >= 71 and <= 77 => "Karlı",
        >= 80 and <= 82 => "Sağanak",
        85 or 86 => "Kar sağanağı",
        >= 95 => "Fırtına",
        _ => "",
    };
}
