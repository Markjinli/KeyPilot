#pragma once

#if defined(_MSC_VER)
typedef unsigned __int8 KEYPILOT_UINT8;
typedef unsigned __int16 KEYPILOT_UINT16;
typedef unsigned __int32 KEYPILOT_UINT32;
typedef unsigned __int64 KEYPILOT_UINT64;
#else
#include <stdint.h>
typedef uint8_t KEYPILOT_UINT8;
typedef uint16_t KEYPILOT_UINT16;
typedef uint32_t KEYPILOT_UINT32;
typedef uint64_t KEYPILOT_UINT64;
#endif

#define KEYPILOT_PROTOCOL_VERSION 2u
#define KEYPILOT_DEVICE_TYPE 0x8000u
#define KEYPILOT_MAX_RULES 512u
#define KEYPILOT_DEVICE_HASH_BYTES 16u
#define KEYPILOT_DEFAULT_LEASE_MILLISECONDS 1500u
#define KEYPILOT_MINIMUM_LEASE_MILLISECONDS 250u
#define KEYPILOT_MAXIMUM_LEASE_MILLISECONDS 5000u

/*
 * A rule whose DeviceHash is all zero matches every keyboard handled by this
 * filter.  A non-zero hash is exact.  The driver reports its computed hash in
 * KEYPILOT_INPUT_EVENT so user mode can replace a discovery rule with an
 * exact rule.  No path, command line, URL, or script is accepted by this ABI.
 */

#ifndef CTL_CODE
#define METHOD_BUFFERED 0u
#define FILE_READ_DATA 0x0001u
#define FILE_WRITE_DATA 0x0002u
#define CTL_CODE(DeviceType, Function, Method, Access) \
    (((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method))
#endif

#define IOCTL_KEYPILOT_GET_CAPABILITIES \
    CTL_CODE(KEYPILOT_DEVICE_TYPE, 0x800u, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_KEYPILOT_ACQUIRE_LEASE \
    CTL_CODE(KEYPILOT_DEVICE_TYPE, 0x801u, METHOD_BUFFERED, FILE_READ_DATA | FILE_WRITE_DATA)
#define IOCTL_KEYPILOT_HEARTBEAT \
    CTL_CODE(KEYPILOT_DEVICE_TYPE, 0x802u, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_KEYPILOT_REPLACE_RULES \
    CTL_CODE(KEYPILOT_DEVICE_TYPE, 0x803u, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_KEYPILOT_READ_EVENTS \
    CTL_CODE(KEYPILOT_DEVICE_TYPE, 0x804u, METHOD_BUFFERED, FILE_READ_DATA)
#define IOCTL_KEYPILOT_RELEASE_LEASE \
    CTL_CODE(KEYPILOT_DEVICE_TYPE, 0x805u, METHOD_BUFFERED, FILE_WRITE_DATA)

typedef enum KEYPILOT_INPUT_PHASE {
    KeyPilotInputDown = 1,
    KeyPilotInputUp = 2,
    KeyPilotInputRepeat = 3
} KEYPILOT_INPUT_PHASE;

typedef enum KEYPILOT_KEYBOARD_FLAGS {
    KeyPilotKeyboardFlagBreak = 0x0001,
    KeyPilotKeyboardFlagE0 = 0x0002,
    KeyPilotKeyboardFlagE1 = 0x0004,
    KeyPilotKeyboardMatchFlags = KeyPilotKeyboardFlagE0 | KeyPilotKeyboardFlagE1
} KEYPILOT_KEYBOARD_FLAGS;

typedef enum KEYPILOT_RULE_FLAGS {
    KeyPilotRuleNone = 0,
    KeyPilotRuleSuppressOriginal = 1u << 0
} KEYPILOT_RULE_FLAGS;

/* Protocol v2 requires RuleFlags == KeyPilotRuleSuppressOriginal exactly. */

typedef enum KEYPILOT_DRIVER_STATE_FLAGS {
    KeyPilotDriverFailOpen = 1u << 0,
    KeyPilotDriverLeaseActive = 1u << 1,
    KeyPilotDriverRulesActive = 1u << 2,
    KeyPilotDriverQueueOverflowed = 1u << 3,
    KeyPilotDriverInputTrackingLost = 1u << 4,
    KeyPilotDriverProgressTimeout = 1u << 5,
    KeyPilotDriverEmergencyBypassActive = 1u << 6
} KEYPILOT_DRIVER_STATE_FLAGS;

#pragma pack(push, 1)

typedef struct KEYPILOT_MESSAGE_HEADER {
    KEYPILOT_UINT32 Size;
    KEYPILOT_UINT32 ProtocolVersion;
} KEYPILOT_MESSAGE_HEADER, *PKEYPILOT_MESSAGE_HEADER;

typedef struct KEYPILOT_CAPABILITIES {
    KEYPILOT_MESSAGE_HEADER Header;
    KEYPILOT_UINT32 DriverVersionMajor;
    KEYPILOT_UINT32 DriverVersionMinor;
    KEYPILOT_UINT32 MaximumRules;
    KEYPILOT_UINT32 StateFlags;
    KEYPILOT_UINT64 ActiveGeneration;
} KEYPILOT_CAPABILITIES, *PKEYPILOT_CAPABILITIES;

typedef struct KEYPILOT_LEASE_REQUEST {
    KEYPILOT_MESSAGE_HEADER Header;
    KEYPILOT_UINT32 RequestedLeaseMilliseconds;
    KEYPILOT_UINT32 Reserved;
    KEYPILOT_UINT64 ClientNonce;
} KEYPILOT_LEASE_REQUEST, *PKEYPILOT_LEASE_REQUEST;

typedef struct KEYPILOT_LEASE_RESPONSE {
    KEYPILOT_MESSAGE_HEADER Header;
    KEYPILOT_UINT32 GrantedLeaseMilliseconds;
    KEYPILOT_UINT32 Reserved;
    KEYPILOT_UINT64 LeaseId;
} KEYPILOT_LEASE_RESPONSE, *PKEYPILOT_LEASE_RESPONSE;

typedef struct KEYPILOT_HEARTBEAT {
    KEYPILOT_MESSAGE_HEADER Header;
    KEYPILOT_UINT64 LeaseId;
    KEYPILOT_UINT64 ExpectedGeneration;
    /* Last contiguous event whose user-mode action completed successfully. */
    KEYPILOT_UINT64 LastAcknowledgedSequence;
} KEYPILOT_HEARTBEAT, *PKEYPILOT_HEARTBEAT;

typedef struct KEYPILOT_RELEASE_REQUEST {
    KEYPILOT_MESSAGE_HEADER Header;
    KEYPILOT_UINT64 LeaseId;
} KEYPILOT_RELEASE_REQUEST, *PKEYPILOT_RELEASE_REQUEST;

typedef struct KEYPILOT_KEYBOARD_RULE {
    KEYPILOT_UINT8 DeviceHash[KEYPILOT_DEVICE_HASH_BYTES];
    KEYPILOT_UINT16 MakeCode;
    KEYPILOT_UINT16 RequiredFlags;
    KEYPILOT_UINT16 IgnoredFlags;
    KEYPILOT_UINT16 Reserved;
    KEYPILOT_UINT32 RuleFlags;
    KEYPILOT_UINT64 RuleId;
} KEYPILOT_KEYBOARD_RULE, *PKEYPILOT_KEYBOARD_RULE;

/*
 * v2 rules are prefix-exact, not flag wildcards. RequiredFlags and
 * IgnoredFlags MUST equal one of these three pairs exactly:
 *   normal: RequiredFlags=0,  IgnoredFlags=E0|E1
 *   E0:     RequiredFlags=E0, IgnoredFlags=E1
 *   E1:     RequiredFlags=E1, IgnoredFlags=E0
 */

typedef struct KEYPILOT_RULESET_HEADER {
    KEYPILOT_MESSAGE_HEADER Header;
    KEYPILOT_UINT64 LeaseId;
    KEYPILOT_UINT64 Generation;
    KEYPILOT_UINT32 RuleCount;
    KEYPILOT_UINT32 RuleSize;
    /* Followed by RuleCount KEYPILOT_KEYBOARD_RULE entries. */
} KEYPILOT_RULESET_HEADER, *PKEYPILOT_RULESET_HEADER;

typedef struct KEYPILOT_INPUT_EVENT {
    KEYPILOT_MESSAGE_HEADER Header;
    KEYPILOT_UINT64 Generation;
    KEYPILOT_UINT64 RuleId;
    KEYPILOT_UINT64 Sequence;
    KEYPILOT_UINT8 DeviceHash[KEYPILOT_DEVICE_HASH_BYTES];
    KEYPILOT_UINT16 MakeCode;
    KEYPILOT_UINT16 Flags;
    KEYPILOT_UINT16 VirtualKey;
    KEYPILOT_UINT16 Phase;
    KEYPILOT_UINT32 Reserved; /* v2: low 16 bits contain KEYBOARD_INPUT_DATA.UnitId. */
} KEYPILOT_INPUT_EVENT, *PKEYPILOT_INPUT_EVENT;

#pragma pack(pop)
