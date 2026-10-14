using System.Globalization;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed class UserContextService
{
    private readonly UserProfileStore _profileStore;
    private readonly IpLocationService _ipLocationService;
    private readonly SemaphoreSlim _locationUpdateLock = new(1, 1);
    private UserProfile? _profile;

    public UserContextService(UserProfileStore profileStore, IpLocationService ipLocationService)
    {
        _profileStore = profileStore;
        _ipLocationService = ipLocationService;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _profile = _profileStore.LoadOrCreate();
        await EnsureAutoLocationAsync(cancellationToken);
    }

    public UserContextSnapshot CreateSnapshot()
    {
        var now = DateTimeOffset.Now;
        var timeZone = TimeZoneInfo.Local;
        var profile = GetProfile();

        return new UserContextSnapshot
        {
            LocalTime = now,
            UtcTime = now.ToUniversalTime(),
            TimeZoneId = timeZone.Id,
            TimeZoneDisplayName = timeZone.DisplayName,
            UtcOffset = FormatUtcOffset(now.Offset),
            Location = CloneKnownLocation(profile.Location),
            WorkloadMode = profile.WorkloadMode
        };
    }

    public string BuildHiddenSystemContext()
    {
        var snapshot = CreateSnapshot();
        var profile = GetProfile();
        var builder = new StringBuilder();
        builder.AppendLine("Служебный контекст AI HUB. Не показывай этот блок пользователю как отдельное сообщение.");
        builder.AppendLine("Карточка пользователя AI HUB уже передана тебе ниже. Это сведения, которые пользователь указал или подтвердил в программе.");
        builder.AppendLine("Если пользователь спрашивает, что ты знаешь о нём, можно кратко ответить на основе этой карточки и честно сказать, каких данных нет.");
        builder.AppendLine("Используй дату, время и примерное местоположение только если это помогает ответу.");
        builder.AppendLine("Не утверждай, что IP-местоположение точное.");

        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Имя или ник пользователя: {profile.DisplayName.Trim()}.");
        }

        AppendProfilePreferences(builder, profile);
        builder.AppendLine(CultureInfo.InvariantCulture, $"Текущая локальная дата и время пользователя: {snapshot.LocalTime:yyyy-MM-dd HH:mm:ss zzz}.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"UTC-время: {snapshot.UtcTime:yyyy-MM-dd HH:mm:ss 'UTC'}.");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Часовой пояс Windows: {snapshot.TimeZoneId} ({snapshot.TimeZoneDisplayName}), UTC{snapshot.UtcOffset}.");

        if (HasKnownLocation(snapshot.Location))
        {
            var location = snapshot.Location;
            builder.AppendLine(CultureInfo.InvariantCulture, $"Примерное местоположение пользователя: {FormatLocation(location)}.");
            builder.AppendLine(CultureInfo.InvariantCulture, $"Источник местоположения: {location!.Source}, режим: {location.Mode}, точность приблизительная.");
        }

        return builder.ToString().Trim();
    }

    public void UpdateProfile(UserProfile profile)
    {
        _profile = profile;
        _profileStore.Save(profile);
    }

    private async Task EnsureAutoLocationAsync(CancellationToken cancellationToken)
    {
        await _locationUpdateLock.WaitAsync(cancellationToken);
        try
        {
            var profile = GetProfile();
            if (string.Equals(profile.Location.Mode, "manual", StringComparison.OrdinalIgnoreCase)
                || HasKnownLocation(profile.Location))
            {
                return;
            }

            var detected = await _ipLocationService.DetectAsync(cancellationToken);
            if (detected is null || !HasKnownLocation(detected))
            {
                return;
            }

            profile.Location = detected;
            _profileStore.Save(profile);
        }
        finally
        {
            _locationUpdateLock.Release();
        }
    }

    public string DisplayName => GetProfile().DisplayName?.Trim() ?? "";

    private UserProfile GetProfile()
    {
        _profile ??= _profileStore.LoadOrCreate();
        return _profile;
    }

    private static bool HasKnownLocation(UserLocation? location)
    {
        return location is not null
            && (!string.IsNullOrWhiteSpace(location.City)
                || !string.IsNullOrWhiteSpace(location.Region)
                || !string.IsNullOrWhiteSpace(location.Country));
    }

    private static UserLocation? CloneKnownLocation(UserLocation location)
    {
        return HasKnownLocation(location)
            ? new UserLocation
            {
                Mode = location.Mode,
                City = location.City,
                Region = location.Region,
                Country = location.Country,
                CountryCode = location.CountryCode,
                Timezone = location.Timezone,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                Source = location.Source,
                UpdatedAt = location.UpdatedAt
            }
            : null;
    }

    private static string FormatLocation(UserLocation? location)
    {
        if (location is null)
        {
            return "неизвестно";
        }

        var parts = new[] { location.City, location.Region, location.Country }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", parts);
    }

    private static void AppendProfilePreferences(StringBuilder builder, UserProfile profile)
    {
        if (UserWorkloadModes.IsKnown(profile.WorkloadMode))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Предпочитаемый режим нагрузки пользователя: {profile.WorkloadMode}.");
        }

        var preferences = new List<string>();
        if (profile.AnswerPreferences.Concise)
        {
            preferences.Add("кратко и по делу");
        }

        if (profile.AnswerPreferences.Detailed)
        {
            preferences.Add("подробно с объяснениями");
        }

        if (profile.AnswerPreferences.SimpleLanguage)
        {
            preferences.Add("простым языком");
        }

        if (profile.AnswerPreferences.StepByStep)
        {
            preferences.Add("по шагам");
        }

        if (profile.AnswerPreferences.Examples)
        {
            preferences.Add("с примерами");
        }

        if (profile.AnswerPreferences.SourcesWhenSearching)
        {
            preferences.Add("с источниками при поиске");
        }

        if (profile.AnswerPreferences.WarnAboutRisks)
        {
            preferences.Add("предупреждать о рисках и сомнениях");
        }

        if (preferences.Count > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Предпочтения пользователя к ответам: {string.Join(", ", preferences)}.");
        }
    }

    private static string FormatUtcOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{absolute:hh\\:mm}");
    }
}
