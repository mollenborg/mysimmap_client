# MySimMap client

Desktop client for [mysimmap.net](https://mysimmap.net). It reads your aircraft position from X-Plane's built-in web API and uploads it to mysimmap.net, so your flight shows up on the live map and gets a shareable link.

Runs on Linux, Windows and macOS (Avalonia / .NET 10).

## Why is this open source?

The released binaries are not code-signed, so Windows and macOS will warn you when you start them. The source is published here so you can see exactly what the app does. If you'd rather not run a binary you didn't build, you are welcome to build the client yourself (see below) – or write your own.

In short, the client:

- connects to X-Plane's web API (default `127.0.0.1:8086`) and subscribes to position, altitude, heading, ground speed and aircraft ICAO type
- sends that, plus your email, callsign and departure/arrival ICAO, to `https://api.mysimmap.net/api/position` with your API key
- saves your email and API key in `config.conf` next to the executable (plain text)

Nothing else is read or sent.

## Usage

1. Create an account on mysimmap.net and copy your API key.
2. Start X-Plane 12 (the web API is on by default, port 8086).
3. Start the client, enter email, API key, callsign and from/to airports, and press **Start**.

## Building

Requires the .NET 10 SDK.

```bash
cd mysimmap_client

# Linux
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ../publish-linux

# Windows
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ../publish-windows

# macOS (Apple Silicon / Intel)
dotnet publish -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ../publish-mac-arm64
dotnet publish -c Release -r osx-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ../publish-mac-x64
```

### macOS: first start

```bash
chmod +x mysimmap_client
xattr -dr com.apple.quarantine mysimmap_client
./mysimmap_client
```

## License

MIT – see [LICENSE](LICENSE). You may use, modify and build your own version of the client. Use of the mysimmap.net service itself requires an account and an API key.
