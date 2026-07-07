# MiSide Multiplayer Relay Server

## Overview
The MiSide Multiplayer Relay Server is a lightweight, standalone console application designed to facilitate network communication for the MiSide Multiplayer Mod. It operates as a central hub, accepting TCP connections from multiple clients and broadcasting incoming messages to all other connected peers.

## Quick Start (Recommended)
For most users, the easiest way to get started is to download the pre-compiled executable directly.

1. Navigate to the **Releases** section on this GitHub repository.
2. Download the latest `MiSideMultiplayerRelayServer.exe`.
3. Place the executable in your preferred folder and run it. 
*(Note: You must have the .NET 6.0 Runtime installed on your machine to run the executable.)*

## Building from Source
If you prefer to compile the server yourself or wish to modify the code, follow these steps:

### Prerequisites
* The [.NET 6.0 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) installed on your system.

### Build Instructions
1. Clone this repository to your local machine.
2. Open a terminal or command prompt in the directory containing the `MiSideMultiplayerRelayServer.csproj` file.
3. Run the following command to build the project in Release mode:
   ```cmd
   dotnet build -c Release

```

4. Once the build succeeds, navigate to `bin/Release/net6.0/` to find your compiled executable.

*(Alternatively, you can run the server directly from the source directory during development by using the `dotnet run` command).*

## Usage

### Starting the Server

By default, launching the executable will start the TCP listener on all network interfaces (`0.0.0.0`) using port `7777`.

To run the server with default settings, simply execute the compiled binary:

```cmd
MiSideMultiplayerRelayServer.exe

```

### Customizing the Port

If you need to host the server on a different port, you can pass the `--port` or `-p` argument followed by your desired port number.

Example:

```cmd
MiSideMultiplayerRelayServer.exe --port 8080

```

or

```cmd
MiSideMultiplayerRelayServer.exe -p 8080

```

### Stopping the Server

The server handles graceful shutdowns. You can stop the listener and cleanly disconnect all clients by using the standard interrupt command (`Ctrl+C`) in the console window.

## Client Configuration

Once the server is running, connecting players must configure their game mod to connect to the host. As noted in the server console upon startup, clients should set their `Networking.ServerHost` configuration to the host machine's LAN IP address or designated VPN/forwarding IP (such as Hamachi).

## Technical Details

* **Architecture**: The server utilizes a thread-safe `ConcurrentDictionary` to manage connected clients, assigning each connection a unique incremental ID.
* **Data Transmission**: Network streams are processed using UTF-8 encoding. The server reads incoming lines of text and immediately broadcasts them to all other active clients, explicitly excluding the original sender to prevent echo loops.
* **Concurrency**: Each client connection is handled asynchronously via the thread pool (`ThreadPool.QueueUserWorkItem`) to ensure smooth, non-blocking message routing.

## Extra Details

Please note that player puppets will not disappear from the current level unless you reload the level, swap levels, or close the game. This will be fixed in a future update for the main mod.

---

*Developed by QuestionBlock64*
