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


## Detailed Host & Client Instructions

**Step 1: Make the relay server listen on all network interfaces**

* Open the folder where the relay server executable is located.
* Find the file named settings.json and open it with Notepad (or any text editor).
* Look for the "Server" section. You should see a line like: "Address": "127.0.0.1". Change it to "127.0.0.1" to "0.0.0.0" so it reads: "Address": "0.0.0.0".
*Save the file and restart the relay server.

**Now the relay server can accept connections from other computers, not just from the same PC.**

---

**Step 2: Open TCP port 7777 in Windows Firewall*

Choose one of the following methods.

**Method A – Command line (fastest)**

* Press the Windows key, type powershell, right‑click Windows PowerShell and select Run as administrator.
* Copy and paste this command, then press Enter:
* 
New-NetFirewallRule -DisplayName "MiSide Relay" -Direction Inbound -Protocol TCP -LocalPort 7777 -Action Allow

* You should see a confirmation message. Close PowerShell.

**Method B – GUI (if you prefer clicking)**

* Press the Windows key, type Windows Defender Firewall, and open it.
* Click Advanced settings on the left side.
* Click Inbound Rules on the left.
* Click New Rule… on the right.
* Choose Port → Next.
* Select TCP and enter 7777 in the Specific local ports field → Next.
* Select Allow the connection → Next.
* Make sure Domain, Private, and Public are all checked → Next.
* Give it a name, e.g., MiSide Relay → Finish.

---

**Step 3: Make sure the relay server is actually running**

* You should see the relay server console window open with the text:
  
  Listening on 0.0.0.0:7777 (TCP)
  

* If you don’t see that, double‑click the relay server executable to start it.

---

**Step 4: Find your local IP address**

* Press Windows key + R, type cmd and press Enter.
* In the black window, type ipconfig and press Enter.
* Look for the section with your active network adapter (Wi‑Fi or Ethernet). You’ll see something like:
* IPv4 Address . . . . . . . . . . . : 192.168.x.xx
  
 That number (192.168.x.xx in this example) is what other players need to put into their Server Host config.

---

**Step 5: Configure the other player's mod**

**On the other player's PC, they must edit their mod config:**

* Open the file BepInEx/config/MS_Multiplayer.cfg with Notepad.
* Under [Networking], change the Server Host line to your IPv4 address from Step 4: Server Host = 192.168.x.xx (Replace 192.168.x.xx with your actual IP.). Make sure Server Port is 7777.
*Save the file and launch the game.


## Technical Details

* **Architecture**: The server utilizes a thread-safe `ConcurrentDictionary` to manage connected clients, assigning each connection a unique incremental ID.
* **Data Transmission**: Network streams are processed using UTF-8 encoding. The server reads incoming lines of text and immediately broadcasts them to all other active clients, explicitly excluding the original sender to prevent echo loops.
* **Concurrency**: Each client connection is handled asynchronously via the thread pool (`ThreadPool.QueueUserWorkItem`) to ensure smooth, non-blocking message routing.

## Extra Details

Please note that player puppets will not disappear from the current level unless you reload the level, swap levels, or close the game. This will be fixed in a future update for the main mod.

---

*Developed by QuestionBlock64*
