# BusPulse UML

Diagrams are Mermaid, so they render on GitHub and in the VS Code Markdown preview. They reflect the code as of 2026-10-20; members are abbreviated to the ones that matter for understanding the design.

## 1. Presentation layer (MVVM)

```mermaid
classDiagram
    direction LR

    class MainWindow {
        +DataContext : ShellViewModel
    }
    class TrayController {
        +ShowRequested
        +ShowAlert(text)
    }
    class ShellViewModel {
        +SelectedTabIndex
        +IsMuted
        +ActiveProfile
        +FlowDirection
        +IsWindowActive
        +ToggleMuteCommand()
        +ToggleLanguageCommand()
        +SelectTabCommand(index)
        +RefreshCommand()
    }
    class StopsViewModel {
        +Stops
        +SelectedAnchor
        +StatusText
        +SetActive(active)
        +LoadCommand()
        +ToggleStopCommand(item)
        +RefreshExpandedAsync()
        +Tick()
    }
    class StopItemViewModel {
        +Name
        +Code
        +DistanceText
        +IsExpanded
        +ArrivalsStatus
    }
    class ArrivalRowViewModel {
        +LineName
        +Destination
        +EtaText
        +BadgeText
        +IsStale
        +Tooltip
        +Refresh(now)
    }
    class TripPlannerViewModel {
        +SelectedStart
        +SelectedDestination
        +LineName
        +StatusText
        +CheckRouteCommand()
    }
    class LinesViewModel {
        +EmptyMessage
    }
    class SettingsViewModel {
        +NewPlaceName
        +NewPlaceStopCode
        +ProviderStatuses
        +AddPlaceCommand()
        +CheckConnectionCommand()
    }
    class IDispatcher {
        <<interface>>
        +Post(action)
    }
    class ILocalizer {
        <<interface>>
        +Get(key)
        +SetLanguage(language)
    }

    MainWindow --> ShellViewModel : binds
    MainWindow --> TrayController
    TrayController --> ShellViewModel : observes
    TrayController --> IDispatcher
    ShellViewModel *-- StopsViewModel
    ShellViewModel *-- TripPlannerViewModel
    ShellViewModel *-- LinesViewModel
    ShellViewModel *-- SettingsViewModel
    ShellViewModel --> ILocalizer
    StopsViewModel *-- "0..*" StopItemViewModel
    StopItemViewModel *-- "0..*" ArrivalRowViewModel
    StopsViewModel --> IDispatcher
    StopsViewModel --> ILocalizer
```

## 2. Live data layer

```mermaid
classDiagram
    direction TB

    class ILiveDataAggregator {
        <<interface>>
        +GetArrivalsAsync(stopCode) AggregatedArrivals
    }
    class LiveDataAggregator {
        -MaxLiveAge = 5 min
        -PickFreshestPerLine()
        -IsValidLive(arrival)
    }
    class ILiveArrivalsProvider {
        <<interface>>
        +Source : ArrivalSource
        +GetArrivalsAsync(stopCode) ProviderResult
    }
    class CurlbusArrivalsProvider {
        -cache 25 s per stop
        -1 request per second
        -circuit breaker 3 failures
        +Parse(json)
    }
    class RailProvider {
        +IsConfigured
        disabled without RailApiKey
    }
    class IScheduleProvider {
        <<interface>>
        +GetScheduleAsync(stopCode) ProviderResult
    }
    class StrideScheduleProvider {
        +ParseStopId(json)
        +ParseRideStops(json)
    }
    class IClock {
        <<interface>>
        +UtcNow
    }
    class Arrival {
        <<record>>
        StopCode
        LineName
        Destination
        Eta
        IsLive
        Source
        ObservedAt
    }
    class ProviderResult {
        <<record>>
        Status
        Arrivals
    }
    class AggregatedArrivals {
        <<record>>
        Arrivals
        ProviderStatuses
    }
    class ArrivalSource {
        <<enumeration>>
        Curlbus
        Rail
        Stride
        Mot
        Schedule
    }
    class ProviderStatus {
        <<enumeration>>
        Ok
        NoData
        Unavailable
        CircuitOpen
        Disabled
    }

    ILiveDataAggregator <|.. LiveDataAggregator
    ILiveArrivalsProvider <|.. CurlbusArrivalsProvider
    ILiveArrivalsProvider <|.. RailProvider
    IScheduleProvider <|.. StrideScheduleProvider
    LiveDataAggregator o-- "1..*" ILiveArrivalsProvider : priority order
    LiveDataAggregator --> IScheduleProvider : fallback
    LiveDataAggregator --> IClock
    CurlbusArrivalsProvider --> IClock
    LiveDataAggregator ..> AggregatedArrivals : returns
    ILiveArrivalsProvider ..> ProviderResult : returns
    ProviderResult o-- "0..*" Arrival
    AggregatedArrivals o-- "0..*" Arrival
    Arrival --> ArrivalSource
    ProviderResult --> ProviderStatus
```

## 3. Services, location and configuration

```mermaid
classDiagram
    direction LR

    class StopsViewModel
    class TripPlannerViewModel
    class SettingsViewModel
    class ILiveDataAggregator {
        <<interface>>
    }
    class ILocationService {
        <<interface>>
        +GetCurrentAsync() LocationResult
    }
    class WindowsLocationService {
        Windows.Devices.Geolocation
    }
    class IStopCatalog {
        <<interface>>
        +GetStopsAsync() Stop[]
    }
    class StrideStopCatalog {
        daily cache in LOCALAPPDATA
    }
    class Geo {
        <<static>>
        +DistanceMeters()
        +FindNearby()
        +IsInIsrael()
    }
    class ISettingsService {
        <<interface>>
        +Config : AppConfig
        +SaveAsync() bool
    }
    class SettingsService
    class AppConfigStore {
        DPAPI CurrentUser
        settings.dpapi
    }
    class AppConfig {
        Language
        ActiveProfile
        NotificationsMuted
        SavedPlaces
        StartPlaceId
        DestinationPlaceId
        LineName
    }
    class SavedPlace {
        Id
        Name
        StopCode
    }
    class SavedPlaceCollection {
        +Items
        +SyncToConfig()
    }
    class SchedulePollingService {
        BackgroundService
    }
    class SoundService {
        plays only when not muted
    }
    class ToastNotificationService {
        builds toast XML only
    }

    StopsViewModel --> ILocationService
    StopsViewModel --> IStopCatalog
    StopsViewModel --> ILiveDataAggregator
    StopsViewModel --> SavedPlaceCollection
    StopsViewModel ..> Geo
    TripPlannerViewModel --> ILiveDataAggregator
    TripPlannerViewModel --> ISettingsService
    TripPlannerViewModel --> SavedPlaceCollection
    SettingsViewModel --> ISettingsService
    SettingsViewModel --> SavedPlaceCollection
    ILocationService <|.. WindowsLocationService
    IStopCatalog <|.. StrideStopCatalog
    ISettingsService <|.. SettingsService
    SettingsService --> AppConfigStore
    SettingsService --> AppConfig
    SavedPlaceCollection --> ISettingsService
    AppConfig o-- "0..30" SavedPlace
    SchedulePollingService --> ILiveDataAggregator
    SchedulePollingService --> AppConfig
    SoundService --> AppConfig
```

## 4. Sequence: opening the Stops tab

```mermaid
sequenceDiagram
    actor User
    participant W as MainWindow
    participant S as ShellViewModel
    participant V as StopsViewModel
    participant C as IStopCatalog
    participant L as ILocationService
    participant A as LiveDataAggregator
    participant P as Curlbus / Schedule

    User->>W: shows the window (Stops tab selected)
    W->>S: IsWindowActive = true
    S->>V: SetActive(true)
    V->>C: GetStopsAsync()
    C-->>V: today's stops (daily cache or download)
    V->>L: GetCurrentAsync()
    L-->>V: position and accuracy, or a failure status
    V->>V: Geo.FindNearby, then update the list in place
    User->>V: expands a stop
    V->>A: GetArrivalsAsync(stopCode)
    A->>P: live providers, then the schedule
    P-->>A: arrivals
    A->>A: drop invalid or stale data, freshest per line, merge schedule
    A-->>V: arrivals marked live or scheduled
    loop every second while active
        V->>V: Tick (countdown, stale check)
    end
    loop every 25 s while active
        V->>A: refresh expanded stops
    end
    User->>W: minimizes
    W->>S: IsWindowActive = false
    S->>V: SetActive(false) stops ticker and polling
```
