# How the Security Features Work - Visual Guide

## 1. Connection Flow with Fail2Ban

```
┌─────────────┐
│   Client    │
│  Attempts   │
│  Connection │
└──────┬──────┘
       │
       ▼
┌─────────────────────────────────┐
│  Is Client Already Banned?      │
│  (Check _bannedClients dict)    │
└────┬─────────────────────┬──────┘
     │ YES                 │ NO
     │                     │
     ▼                     ▼
┌─────────────┐     ┌────────────────────┐
│   REJECT    │     │  Check Blocklist   │
│ (Banned)    │     │  (BlockedClients,  │
└─────────────┘     │  KnownBadActors)   │
                    └────┬───────────┬────┘
                         │ FOUND     │ NOT FOUND
                         │           │
                         ▼           ▼
                    ┌─────────┐  ┌──────────────────┐
                    │ REJECT  │  │ Check Allowlist  │
                    │(Blocked)│  │(if configured)   │
                    └─────────┘  └────┬────────┬────┘
                                      │ PASS   │ FAIL
                                      │        │
                                      ▼        ▼
                              ┌──────────┐  ┌────────┐
                              │Auth Check│  │ REJECT │
                              └────┬─────┘  └────────┘
                                   │
                    ┌──────────────┴──────────────┐
                    │                             │
                    ▼                             ▼
              ┌──────────┐                  ┌───────────────┐
              │Auth Fail │                  │  Auth Pass    │
              └────┬─────┘                  └───────────────┘
                   │                              │
                   ▼                              ▼
        ┌─────────────────────┐          ┌──────────────┐
        │ Record Failed       │          │   ACCEPT     │
        │ Attempt in          │          │ Connection   │
        │ _failedAttempts     │          └──────────────┘
        └──────┬──────────────┘
               │
               ▼
        ┌────────────────────┐
        │ Count >= Max       │
        │ Attempts?          │
        └──┬──────────────┬──┘
           │ YES          │ NO
           │              │
           ▼              ▼
    ┌──────────┐    ┌─────────┐
    │   BAN    │    │ REJECT  │
    │  Client  │    │ Only    │
    └──────────┘    └─────────┘
```

## 2. Packet Processing Flow

```
┌──────────────┐
│ MQTT Packet  │
│   Arrives    │
└──────┬───────┘
       │
       ▼
┌─────────────────────────┐
│ Global Rate Limit OK?   │
│ (Max packets/min total) │
└──┬──────────────────┬───┘
   │ NO               │ YES
   │                  │
   ▼                  ▼
┌──────┐      ┌──────────────────┐
│ DROP │      │ Calculate Packet │
└──────┘      │ Hash (SHA256)    │
              └────────┬─────────┘
                       │
                       ▼
              ┌────────────────────┐
              │ Is Duplicate?      │
              │ (Hash seen before  │
              │ within window?)    │
              └──┬────────────┬────┘
                 │ YES        │ NO
                 │            │
                 ▼            ▼
            ┌───────┐   ┌────────────────┐
            │  DROP │   │ Extract Node ID│
            └───────┘   └────────┬───────┘
                                 │
                                 ▼
                        ┌────────────────────┐
                        │ Is Node Banned?    │
                        │ (Rate limit ban)   │
                        └──┬─────────────┬───┘
                           │ YES         │ NO
                           │             │
                           ▼             ▼
                      ┌───────┐   ┌─────────────────┐
                      │  DROP │   │ Node Packet     │
                      └───────┘   │ Count OK?       │
                                  │ (Max/min)       │
                                  └──┬──────────┬───┘
                                     │ NO       │ YES
                                     │          │
                                     ▼          ▼
                              ┌──────────┐  ┌──────────────┐
                              │BAN NODE  │  │Check Topic   │
                              │& DROP    │  │Allowed?      │
                              └──────────┘  └──┬────────┬──┘
                                               │ NO     │ YES
                                               │        │
                                               ▼        ▼
                                          ┌───────┐  ┌────────────┐
                                          │ DROP  │  │Check Port  │
                                          └───────┘  │Number OK?  │
                                                     └──┬────────┬┘
                                                        │ NO     │ YES
                                                        │        │
                                                        ▼        ▼
                                                   ┌───────┐  ┌──────────┐
                                                   │ DROP  │  │Check Hop │
                                                   └───────┘  │Count OK? │
                                                              └──┬────┬──┘
                                                                 │NO  │YES
                                                                 │    │
                                                                 ▼    ▼
                                                            ┌────┐  ┌───────────┐
                                                            │DROP│  │  ACCEPT   │
                                                            └────┘  │& FORWARD  │
                                                                    └───────────┘
```

## 3. Rate Limiting Data Structures

### Duplicate Detection
```
_seenPackets: ConcurrentDictionary<string, DateTime>
┌─────────────────────────────────────────────┐
│ PacketHash                    FirstSeen     │
├─────────────────────────────────────────────┤
│ "abc123..."                   2024-12-23    │
│                               14:30:00      │
│ "def456..."                   2024-12-23    │
│                               14:30:15      │
│ "ghi789..."                   2024-12-23    │
│                               14:30:45      │
└─────────────────────────────────────────────┘
        │
        ├─> New packet with hash "abc123..." arrives
        ├─> Check timestamp: Is (now - 14:30:00) < 5 minutes?
        ├─> YES → DUPLICATE, DROP
        └─> NO  → Update timestamp, ALLOW
```

### Per-Node Rate Limiting
```
_nodePackets: ConcurrentDictionary<string, List<DateTime>>
┌───────────────────────────────────────────────┐
│ NodeID        Packet Timestamps (last minute) │
├───────────────────────────────────────────────┤
│ "!12345678"   [14:29:10, 14:29:25, 14:29:40,  │
│               14:30:00, 14:30:15, 14:30:30]   │
│                       ↑                        │
│                    6 packets in last minute   │
│                                                │
│ "!87654321"   [14:30:00, 14:30:05, 14:30:10,  │
│               14:30:15, 14:30:20, 14:30:25,   │
│               ... 55 more timestamps ...]     │
│                       ↑                        │
│                    61 packets = OVER LIMIT    │
│                    → BAN NODE                 │
└───────────────────────────────────────────────┘
```

### Banned Nodes
```
_bannedNodes: ConcurrentDictionary<string, DateTime>
┌─────────────────────────────────────┐
│ NodeID        Ban Expires At        │
├─────────────────────────────────────┤
│ "!87654321"   2024-12-23 15:00:00  │
│ "!11111111"   2024-12-23 14:45:30  │
└─────────────────────────────────────┘
        │
        ├─> Node "!87654321" tries to send at 14:50:00
        ├─> Check: Is 14:50:00 < 15:00:00?
        ├─> YES → Still banned, DROP
        └─> Will unban at 15:00:00
```

## 4. Topic Pattern Matching

### MQTT Wildcards to Regex Conversion
```
MQTT Pattern:       "msh/+/2/e/+/+"
                         ↓
Regex Pattern:      "^msh/[^/]+/2/e/[^/]+/[^/]+$"
                         ↓
Matches:            "msh/US/2/e/LongFast/!12345678"  ✓
                    "msh/EU/2/e/ShortFast/!99999999" ✓
Doesn't Match:      "msh/US/3/e/LongFast/!12345678"  ✗ (wrong version)
                    "msh/US/2/json/mqtt/!12345678"   ✗ (json not e)
                    "test/random/topic"              ✗ (wrong format)

MQTT Pattern:       "msh/#"
                         ↓
Regex Pattern:      "^msh/.*$"
                         ↓
Matches:            Anything starting with "msh/"
```

## 5. Example Scenario: Malicious Client

```
Timeline of Events:

14:00:00  Client "evil_client" connects
          └─> Not in blocklist, ACCEPT

14:00:05  Sends 100 packets/second from node "!badnode"
          └─> After 1 second, exceeds per-node limit (60/min)
          └─> Node "!badnode" BANNED for 30 minutes
          └─> All packets from "!badnode" DROPPED until 14:30:05

14:00:10  Client reconnects as "evil_client2"
          └─> Tries wrong password 3 times rapidly
          └─> Fail2Ban records attempts
          
14:00:15  Client "evil_client2" tries again with wrong password
          └─> 4th failed attempt
          
14:00:20  Client "evil_client2" tries again
          └─> 5th failed attempt
          └─> THRESHOLD REACHED
          └─> "evil_client2" BANNED for 60 minutes
          └─> All connection attempts from "evil_client2" REJECTED until 15:00:20

14:05:00  Client "evil_client3" tries to publish to "hack/malicious/topic"
          └─> Topic not in AllowedTopics list
          └─> Packet DROPPED
          
14:10:00  Client "evil_client3" sends encrypted packet from unknown channel
          └─> Packet cannot be decrypted
          └─> BlockUndecryptablePackets = true
          └─> Packet DROPPED

Result: Network protected from:
- Rate limit abuse ✓
- Brute force attacks ✓
- Topic abuse ✓
- Unknown channel spam ✓
```

## 6. Configuration Impact Visualization

### Strict Config (High Security)
```
┌────────────────────────────────────┐
│          100 Packets Arrive        │
└────────────────┬───────────────────┘
                 │
    ┌────────────┼────────────┐
    │            │            │
    ▼            ▼            ▼
┌────────┐  ┌────────┐  ┌────────┐
│ Fail2  │  │ Rate   │  │ Topic  │
│ Ban    │  │ Limit  │  │ Filter │
│        │  │        │  │        │
│ -5     │  │ -20    │  │ -30    │
└────────┘  └────────┘  └────────┘
    │            │            │
    └────────────┼────────────┘
                 │
                 ▼
        ┌────────────────┐
        │  45 Packets    │
        │  Forwarded     │
        │  (45% pass)    │
        └────────────────┘
```

### Permissive Config (Low Security)
```
┌────────────────────────────────────┐
│          100 Packets Arrive        │
└────────────────┬───────────────────┘
                 │
    ┌────────────┼────────────┐
    │            │            │
    ▼            ▼            ▼
┌────────┐  ┌────────┐  ┌────────┐
│ Fail2  │  │ Rate   │  │ Topic  │
│ Ban    │  │ Limit  │  │ Filter │
│Disabled│  │        │  │Disabled│
│ -0     │  │ -5     │  │ -0     │
└────────┘  └────────┘  └────────┘
    │            │            │
    └────────────┼────────────┘
                 │
                 ▼
        ┌────────────────┐
        │  95 Packets    │
        │  Forwarded     │
        │  (95% pass)    │
        └────────────────┘
```
