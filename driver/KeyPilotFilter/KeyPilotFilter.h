#pragma once

#include <ntddk.h>
#include <wdf.h>
#include <kbdmou.h>
#include <ntddkbd.h>

#include "..\include\KeyPilotProtocol.h"

C_ASSERT(sizeof(KEYPILOT_MESSAGE_HEADER) == 8);
C_ASSERT(sizeof(KEYPILOT_CAPABILITIES) == 32);
C_ASSERT(sizeof(KEYPILOT_LEASE_REQUEST) == 24);
C_ASSERT(sizeof(KEYPILOT_LEASE_RESPONSE) == 24);
C_ASSERT(sizeof(KEYPILOT_HEARTBEAT) == 32);
C_ASSERT(sizeof(KEYPILOT_RELEASE_REQUEST) == 16);
C_ASSERT(sizeof(KEYPILOT_KEYBOARD_RULE) == 36);
C_ASSERT(sizeof(KEYPILOT_RULESET_HEADER) == 32);
C_ASSERT(sizeof(KEYPILOT_INPUT_EVENT) == 60);

#define KEYPILOT_NT_DEVICE_NAME L"\\Device\\KeyPilotControl"
#define KEYPILOT_DOS_DEVICE_NAME L"\\DosDevices\\Global\\KeyPilot"
#define KEYPILOT_EVENT_CAPACITY 1024u
#define KEYPILOT_MAX_EVENTS_PER_READ 64u
#define KEYPILOT_DRIVER_VERSION_MAJOR 0u
#define KEYPILOT_DRIVER_VERSION_MINOR 2u
#define KEYPILOT_EMERGENCY_HOLD_MILLISECONDS 2000u

typedef struct KEYPILOT_PRESS_STATE {
    BOOLEAN Active;
    BOOLEAN Suppress;
    USHORT UnitId;
    USHORT MakeCode;
    USHORT MatchFlags;
    ULONGLONG LeaseId;
    ULONGLONG Generation;
    ULONGLONG RuleId;
} KEYPILOT_PRESS_STATE, *PKEYPILOT_PRESS_STATE;

typedef struct KEYPILOT_DRIVER_CONTEXT KEYPILOT_DRIVER_CONTEXT;
typedef KEYPILOT_DRIVER_CONTEXT *PKEYPILOT_DRIVER_CONTEXT;

typedef struct KEYPILOT_DEVICE_CONTEXT {
    CONNECT_DATA UpperConnectData;
    BOOLEAN Connected;
    BOOLEAN TrackingLost;
    BOOLEAN EmergencyLeftCtrlDown;
    BOOLEAN EmergencyLeftShiftDown;
    BOOLEAN EmergencyF12Down;
    BOOLEAN EmergencyTimerArmed;
    BOOLEAN EmergencyBypassLatched;
    WDFTIMER EmergencyBypassTimer;
    UCHAR DeviceHash[KEYPILOT_DEVICE_HASH_BYTES];
    PKEYPILOT_DRIVER_CONTEXT DriverContext;
    KEYPILOT_PRESS_STATE Presses[KEYPILOT_MAX_RULES];
} KEYPILOT_DEVICE_CONTEXT, *PKEYPILOT_DEVICE_CONTEXT;

typedef struct KEYPILOT_DRIVER_CONTEXT {
    WDFSPINLOCK PolicyLock;
    WDFDEVICE ControlDevice;
    WDFFILEOBJECT LeaseOwner;
    ULONGLONG LeaseId;
    ULONGLONG LeaseDeadline100ns;
    ULONGLONG ProgressDeadline100ns;
    ULONGLONG ActiveGeneration;
    ULONGLONG NextLeaseSequence;
    ULONGLONG NextEventSequence;
    ULONGLONG LastDeliveredSequence;
    ULONGLONG LastAcknowledgedSequence;
    ULONG GrantedLeaseMilliseconds;
    ULONG ActiveRuleBuffer;
    ULONG RuleCount;
    ULONG StickyStateFlags;
    ULONG TrackingLostDeviceCount;
    ULONG EmergencyBypassDeviceCount;
    KEYPILOT_KEYBOARD_RULE RuleBuffers[2][KEYPILOT_MAX_RULES];
    KEYPILOT_INPUT_EVENT Events[KEYPILOT_EVENT_CAPACITY];
    ULONG EventHead;
    ULONG EventCount;
} KEYPILOT_DRIVER_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(KEYPILOT_DRIVER_CONTEXT, KeyPilotGetDriverContext)
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(KEYPILOT_DEVICE_CONTEXT, KeyPilotGetDeviceContext)

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD KeyPilotEvtDeviceAdd;
EVT_WDF_DRIVER_UNLOAD KeyPilotEvtDriverUnload;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL KeyPilotEvtIoInternalDeviceControl;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL KeyPilotEvtIoDeviceControl;
EVT_WDF_FILE_CLEANUP KeyPilotEvtFileCleanup;
EVT_WDF_DEVICE_D0_ENTRY KeyPilotEvtDeviceD0Entry;
EVT_WDF_DEVICE_D0_EXIT KeyPilotEvtDeviceD0Exit;
EVT_WDF_TIMER KeyPilotEvtEmergencyBypassTimer;
EVT_WDF_OBJECT_CONTEXT_CLEANUP KeyPilotEvtDeviceContextCleanup;
EVT_WDF_REQUEST_COMPLETION_ROUTINE KeyPilotConnectCompletion;

VOID
KeyPilotServiceCallback(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_ PULONG InputDataConsumed
    );
