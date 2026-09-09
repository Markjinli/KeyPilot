#include "KeyPilotFilter.h"
#include <initguid.h>
#include <devpkey.h>

static VOID KeyPilotEnterFailOpenLocked(_Inout_ PKEYPILOT_DRIVER_CONTEXT Context, _In_ ULONG StickyFlags);
static BOOLEAN KeyPilotLeaseIsValidLocked(_Inout_ PKEYPILOT_DRIVER_CONTEXT Context, _In_ ULONGLONG Now);
static NTSTATUS KeyPilotCreateControlDevice(_In_ WDFDRIVER Driver);
static VOID KeyPilotInitializeDeviceHash(_In_ WDFDEVICE Device, _Out_writes_bytes_(KEYPILOT_DEVICE_HASH_BYTES) UCHAR *Hash);
static VOID KeyPilotLoseTrackingLocked(_Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext);
static VOID KeyPilotRecoverTrackingLocked(_Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext);
static VOID KeyPilotResetEmergencyChordLocked(_Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext);

static BOOLEAN
KeyPilotHashIsZero(
    _In_reads_bytes_(KEYPILOT_DEVICE_HASH_BYTES) const UCHAR *Hash
    )
{
    ULONG index;
    UCHAR combined = 0;

    for (index = 0; index < KEYPILOT_DEVICE_HASH_BYTES; ++index) {
        combined |= Hash[index];
    }

    return combined == 0;
}

static BOOLEAN
KeyPilotHashesEqual(
    _In_reads_bytes_(KEYPILOT_DEVICE_HASH_BYTES) const UCHAR *Left,
    _In_reads_bytes_(KEYPILOT_DEVICE_HASH_BYTES) const UCHAR *Right
    )
{
    return RtlCompareMemory(Left, Right, KEYPILOT_DEVICE_HASH_BYTES) == KEYPILOT_DEVICE_HASH_BYTES;
}

static BOOLEAN
KeyPilotHeaderIsExact(
    _In_ const KEYPILOT_MESSAGE_HEADER *Header,
    _In_ size_t ExpectedSize,
    _In_ size_t ActualSize
    )
{
    return Header != NULL &&
           ActualSize == ExpectedSize &&
           Header->Size == ExpectedSize &&
           Header->ProtocolVersion == KEYPILOT_PROTOCOL_VERSION;
}

static VOID
KeyPilotEnterFailOpenLocked(
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ ULONG StickyFlags
    )
{
    Context->LeaseOwner = NULL;
    Context->LeaseId = 0;
    Context->LeaseDeadline100ns = 0;
    Context->ProgressDeadline100ns = 0;
    Context->GrantedLeaseMilliseconds = 0;
    Context->RuleCount = 0;
    Context->StickyStateFlags |= StickyFlags;
}

static VOID
KeyPilotResetEmergencyChordLocked(
    _Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext
    )
{
    PKEYPILOT_DRIVER_CONTEXT context = DeviceContext->DriverContext;

    DeviceContext->EmergencyLeftCtrlDown = FALSE;
    DeviceContext->EmergencyLeftShiftDown = FALSE;
    DeviceContext->EmergencyF12Down = FALSE;
    DeviceContext->EmergencyTimerArmed = FALSE;
    if (DeviceContext->EmergencyBypassLatched) {
        NT_ASSERT(context->EmergencyBypassDeviceCount != 0);
        if (context->EmergencyBypassDeviceCount != 0) {
            context->EmergencyBypassDeviceCount--;
        }
        DeviceContext->EmergencyBypassLatched = FALSE;
    }
}

static VOID
KeyPilotLoseTrackingLocked(
    _Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext
    )
{
    PKEYPILOT_DRIVER_CONTEXT context = DeviceContext->DriverContext;

    RtlZeroMemory(DeviceContext->Presses, sizeof(DeviceContext->Presses));
    KeyPilotResetEmergencyChordLocked(DeviceContext);
    if (!DeviceContext->TrackingLost) {
        DeviceContext->TrackingLost = TRUE;
        context->TrackingLostDeviceCount++;
    }
    KeyPilotEnterFailOpenLocked(context, KeyPilotDriverInputTrackingLost);
}

static VOID
KeyPilotRecoverTrackingLocked(
    _Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext
    )
{
    PKEYPILOT_DRIVER_CONTEXT context = DeviceContext->DriverContext;

    RtlZeroMemory(DeviceContext->Presses, sizeof(DeviceContext->Presses));
    KeyPilotResetEmergencyChordLocked(DeviceContext);
    if (DeviceContext->TrackingLost) {
        NT_ASSERT(context->TrackingLostDeviceCount != 0);
        if (context->TrackingLostDeviceCount != 0) {
            context->TrackingLostDeviceCount--;
        }
        DeviceContext->TrackingLost = FALSE;
        if (context->TrackingLostDeviceCount == 0) {
            context->StickyStateFlags &= ~KeyPilotDriverInputTrackingLost;
        }
    }
}

static BOOLEAN
KeyPilotLeaseIsValidLocked(
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ ULONGLONG Now
    )
{
    ULONG stickyFlags = 0;

    if (Context->TrackingLostDeviceCount != 0) {
        stickyFlags |= KeyPilotDriverInputTrackingLost;
    }
    if (Context->ProgressDeadline100ns != 0 && Now >= Context->ProgressDeadline100ns) {
        stickyFlags |= KeyPilotDriverProgressTimeout;
    }
    if (Context->LeaseOwner == NULL || Context->LeaseId == 0 ||
        Context->EmergencyBypassDeviceCount != 0 ||
        Now >= Context->LeaseDeadline100ns || stickyFlags != 0) {
        KeyPilotEnterFailOpenLocked(Context, stickyFlags);
        return FALSE;
    }

    return TRUE;
}

static ULONG
KeyPilotStateFlagsLocked(
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ ULONGLONG Now
    )
{
    ULONG flags;
    BOOLEAN leaseIsValid = KeyPilotLeaseIsValidLocked(Context, Now);

    flags = Context->StickyStateFlags;
    if (Context->EmergencyBypassDeviceCount != 0) {
        flags |= KeyPilotDriverEmergencyBypassActive;
    }
    if (leaseIsValid) {
        flags |= KeyPilotDriverLeaseActive;
        if (Context->RuleCount != 0) {
            flags |= KeyPilotDriverRulesActive;
        } else {
            flags |= KeyPilotDriverFailOpen;
        }
    } else {
        flags |= KeyPilotDriverFailOpen;
    }

    return flags;
}

static BOOLEAN
KeyPilotQueueEventLocked(
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_reads_bytes_(KEYPILOT_DEVICE_HASH_BYTES) const UCHAR *DeviceHash,
    _In_ const KEYBOARD_INPUT_DATA *Input,
    _In_ USHORT Phase,
    _In_ ULONGLONG Generation,
    _In_ ULONGLONG RuleId,
    _In_ ULONGLONG Now
    )
{
    ULONG tail;
    PKEYPILOT_INPUT_EVENT eventRecord;

    if (Context->EventCount >= KEYPILOT_EVENT_CAPACITY) {
        KeyPilotEnterFailOpenLocked(Context, KeyPilotDriverQueueOverflowed);
        return FALSE;
    }

    tail = (Context->EventHead + Context->EventCount) % KEYPILOT_EVENT_CAPACITY;
    eventRecord = &Context->Events[tail];
    RtlZeroMemory(eventRecord, sizeof(*eventRecord));
    eventRecord->Header.Size = sizeof(*eventRecord);
    eventRecord->Header.ProtocolVersion = KEYPILOT_PROTOCOL_VERSION;
    eventRecord->Generation = Generation;
    eventRecord->RuleId = RuleId;
    eventRecord->Sequence = ++Context->NextEventSequence;
    RtlCopyMemory(eventRecord->DeviceHash, DeviceHash, KEYPILOT_DEVICE_HASH_BYTES);
    eventRecord->MakeCode = Input->MakeCode;
    eventRecord->Flags = Input->Flags;
    eventRecord->VirtualKey = 0;
    eventRecord->Phase = Phase;
    eventRecord->Reserved = Input->UnitId;
    Context->EventCount++;
    if (Context->ProgressDeadline100ns == 0) {
        Context->ProgressDeadline100ns =
            Now + ((ULONGLONG)Context->GrantedLeaseMilliseconds * 10000ull);
    }
    return TRUE;
}

static PKEYPILOT_PRESS_STATE
KeyPilotFindPress(
    _Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext,
    _In_ USHORT UnitId,
    _In_ USHORT MakeCode,
    _In_ USHORT MatchFlags
    )
{
    ULONG index;

    for (index = 0; index < KEYPILOT_MAX_RULES; ++index) {
        PKEYPILOT_PRESS_STATE press = &DeviceContext->Presses[index];
        if (press->Active && press->UnitId == UnitId &&
            press->MakeCode == MakeCode && press->MatchFlags == MatchFlags) {
            return press;
        }
    }

    return NULL;
}

static PKEYPILOT_PRESS_STATE
KeyPilotFindFreePress(
    _Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext
    )
{
    ULONG index;

    for (index = 0; index < KEYPILOT_MAX_RULES; ++index) {
        if (!DeviceContext->Presses[index].Active) {
            return &DeviceContext->Presses[index];
        }
    }

    return NULL;
}

static const KEYPILOT_KEYBOARD_RULE *
KeyPilotFindRuleLocked(
    _In_ const PKEYPILOT_DRIVER_CONTEXT DriverContext,
    _In_reads_bytes_(KEYPILOT_DEVICE_HASH_BYTES) const UCHAR *DeviceHash,
    _In_ USHORT MakeCode,
    _In_ USHORT MatchFlags
    )
{
    ULONG index;
    const KEYPILOT_KEYBOARD_RULE *rules = DriverContext->RuleBuffers[DriverContext->ActiveRuleBuffer];

    for (index = 0; index < DriverContext->RuleCount; ++index) {
        const KEYPILOT_KEYBOARD_RULE *rule = &rules[index];
        BOOLEAN deviceMatches = KeyPilotHashIsZero(rule->DeviceHash) ||
                                KeyPilotHashesEqual(rule->DeviceHash, DeviceHash);

        if (deviceMatches &&
            rule->MakeCode == MakeCode &&
            (MatchFlags & rule->RequiredFlags) == rule->RequiredFlags &&
            (MatchFlags & rule->IgnoredFlags) == 0) {
            return rule;
        }
    }

    return NULL;
}

static BOOLEAN
KeyPilotUpdateEmergencyChordLocked(
    _Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext,
    _In_ const KEYBOARD_INPUT_DATA *Input
    )
{
    PKEYPILOT_DRIVER_CONTEXT context = DeviceContext->DriverContext;
    const USHORT matchFlags = Input->Flags & KeyPilotKeyboardMatchFlags;
    const BOOLEAN isBreak = (Input->Flags & KeyPilotKeyboardFlagBreak) != 0;
    const BOOLEAN isLeftCtrl = Input->MakeCode == 0x001d && matchFlags == 0;
    const BOOLEAN isLeftShift = Input->MakeCode == 0x002a && matchFlags == 0;
    const BOOLEAN isF12 = Input->MakeCode == 0x0058 && matchFlags == 0;
    BOOLEAN complete;

    if (!isLeftCtrl && !isLeftShift && !isF12) {
        return FALSE;
    }

    if (isLeftCtrl) {
        DeviceContext->EmergencyLeftCtrlDown = !isBreak;
    } else if (isLeftShift) {
        DeviceContext->EmergencyLeftShiftDown = !isBreak;
    } else {
        DeviceContext->EmergencyF12Down = !isBreak;
    }

    complete = DeviceContext->EmergencyLeftCtrlDown &&
               DeviceContext->EmergencyLeftShiftDown &&
               DeviceContext->EmergencyF12Down;
    if (complete && !DeviceContext->EmergencyTimerArmed &&
        !DeviceContext->EmergencyBypassLatched) {
        DeviceContext->EmergencyTimerArmed = TRUE;
        (VOID)WdfTimerStart(
            DeviceContext->EmergencyBypassTimer,
            WDF_REL_TIMEOUT_IN_MS(KEYPILOT_EMERGENCY_HOLD_MILLISECONDS));
    } else if (!complete) {
        if (DeviceContext->EmergencyTimerArmed) {
            DeviceContext->EmergencyTimerArmed = FALSE;
            (VOID)WdfTimerStop(DeviceContext->EmergencyBypassTimer, FALSE);
        }
        if (DeviceContext->EmergencyBypassLatched) {
            NT_ASSERT(context->EmergencyBypassDeviceCount != 0);
            if (context->EmergencyBypassDeviceCount != 0) {
                context->EmergencyBypassDeviceCount--;
            }
            DeviceContext->EmergencyBypassLatched = FALSE;
        }
    }

    /* Every member of the emergency chord is always delivered to kbdclass. */
    return TRUE;
}

VOID
KeyPilotEvtEmergencyBypassTimer(
    _In_ WDFTIMER Timer
    )
{
    WDFDEVICE device = (WDFDEVICE)WdfTimerGetParentObject(Timer);
    PKEYPILOT_DEVICE_CONTEXT deviceContext = KeyPilotGetDeviceContext(device);
    PKEYPILOT_DRIVER_CONTEXT driverContext = deviceContext->DriverContext;

    WdfSpinLockAcquire(driverContext->PolicyLock);
    if (deviceContext->EmergencyTimerArmed &&
        deviceContext->EmergencyLeftCtrlDown &&
        deviceContext->EmergencyLeftShiftDown &&
        deviceContext->EmergencyF12Down &&
        !deviceContext->EmergencyBypassLatched) {
        deviceContext->EmergencyTimerArmed = FALSE;
        deviceContext->EmergencyBypassLatched = TRUE;
        driverContext->EmergencyBypassDeviceCount++;
        KeyPilotEnterFailOpenLocked(driverContext, 0);
    }
    WdfSpinLockRelease(driverContext->PolicyLock);
}

static BOOLEAN
KeyPilotShouldSuppress(
    _Inout_ PKEYPILOT_DEVICE_CONTEXT DeviceContext,
    _In_ const KEYBOARD_INPUT_DATA *Input
    )
{
    PKEYPILOT_DRIVER_CONTEXT driverContext = DeviceContext->DriverContext;
    const USHORT matchFlags = Input->Flags & KeyPilotKeyboardMatchFlags;
    const BOOLEAN isBreak = (Input->Flags & KeyPilotKeyboardFlagBreak) != 0;
    const KEYPILOT_KEYBOARD_RULE *rule;
    PKEYPILOT_PRESS_STATE press;
    BOOLEAN suppress = FALSE;
    BOOLEAN queued;
    ULONGLONG now = KeQueryInterruptTime();

    WdfSpinLockAcquire(driverContext->PolicyLock);

    if (Input->MakeCode == KEYBOARD_OVERRUN_MAKE_CODE) {
        KeyPilotLoseTrackingLocked(DeviceContext);
        WdfSpinLockRelease(driverContext->PolicyLock);
        return FALSE;
    }

    if (KeyPilotUpdateEmergencyChordLocked(DeviceContext, Input)) {
        WdfSpinLockRelease(driverContext->PolicyLock);
        return FALSE;
    }

    if (!KeyPilotLeaseIsValidLocked(driverContext, now) || DeviceContext->TrackingLost) {
        press = KeyPilotFindPress(DeviceContext, Input->UnitId, Input->MakeCode, matchFlags);
        if (isBreak) {
            if (press != NULL) {
                RtlZeroMemory(press, sizeof(*press));
            }
        } else if (press == NULL && !DeviceContext->TrackingLost) {
            press = KeyPilotFindFreePress(DeviceContext);
            if (press != NULL) {
                RtlZeroMemory(press, sizeof(*press));
                press->Active = TRUE;
                press->UnitId = Input->UnitId;
                press->MakeCode = Input->MakeCode;
                press->MatchFlags = matchFlags;
            } else {
                KeyPilotLoseTrackingLocked(DeviceContext);
            }
        }
        WdfSpinLockRelease(driverContext->PolicyLock);
        return FALSE;
    }

    press = KeyPilotFindPress(DeviceContext, Input->UnitId, Input->MakeCode, matchFlags);

    if (isBreak) {
        if (press == NULL || press->LeaseId != driverContext->LeaseId) {
            if (press != NULL) {
                RtlZeroMemory(press, sizeof(*press));
            }
            WdfSpinLockRelease(driverContext->PolicyLock);
            return FALSE;
        }

        if (press->RuleId == 0) {
            RtlZeroMemory(press, sizeof(*press));
            WdfSpinLockRelease(driverContext->PolicyLock);
            return FALSE;
        }

        queued = KeyPilotQueueEventLocked(
            driverContext,
            DeviceContext->DeviceHash,
            Input,
            KeyPilotInputUp,
            press->Generation,
            press->RuleId,
            now);
        if (queued && driverContext->LeaseId == press->LeaseId) {
            suppress = press->Suppress;
        }
        RtlZeroMemory(press, sizeof(*press));
        WdfSpinLockRelease(driverContext->PolicyLock);
        return suppress;
    }

    if (press != NULL) {
        if (press->LeaseId != driverContext->LeaseId) {
            WdfSpinLockRelease(driverContext->PolicyLock);
            return FALSE;
        } else if (press->RuleId == 0) {
            WdfSpinLockRelease(driverContext->PolicyLock);
            return FALSE;
        } else {
            queued = KeyPilotQueueEventLocked(
                driverContext,
                DeviceContext->DeviceHash,
                Input,
                KeyPilotInputRepeat,
                press->Generation,
                press->RuleId,
                now);
            if (queued && driverContext->LeaseId == press->LeaseId) {
                suppress = press->Suppress;
            }
            WdfSpinLockRelease(driverContext->PolicyLock);
            return suppress;
        }
    }

    if (driverContext->RuleCount == 0) {
        press = KeyPilotFindFreePress(DeviceContext);
        if (press != NULL) {
            RtlZeroMemory(press, sizeof(*press));
            press->Active = TRUE;
            press->UnitId = Input->UnitId;
            press->MakeCode = Input->MakeCode;
            press->MatchFlags = matchFlags;
            press->LeaseId = driverContext->LeaseId;
        } else {
            KeyPilotLoseTrackingLocked(DeviceContext);
        }
        WdfSpinLockRelease(driverContext->PolicyLock);
        return FALSE;
    }

    rule = KeyPilotFindRuleLocked(driverContext, DeviceContext->DeviceHash, Input->MakeCode, matchFlags);
    if (rule == NULL) {
        press = KeyPilotFindFreePress(DeviceContext);
        if (press != NULL) {
            RtlZeroMemory(press, sizeof(*press));
            press->Active = TRUE;
            press->UnitId = Input->UnitId;
            press->MakeCode = Input->MakeCode;
            press->MatchFlags = matchFlags;
            press->LeaseId = driverContext->LeaseId;
        } else {
            KeyPilotLoseTrackingLocked(DeviceContext);
        }
        WdfSpinLockRelease(driverContext->PolicyLock);
        return FALSE;
    }

    press = KeyPilotFindFreePress(DeviceContext);
    if (press == NULL) {
        KeyPilotLoseTrackingLocked(DeviceContext);
        WdfSpinLockRelease(driverContext->PolicyLock);
        return FALSE;
    }

    RtlZeroMemory(press, sizeof(*press));
    press->Active = TRUE;
    press->Suppress = (rule->RuleFlags & KeyPilotRuleSuppressOriginal) != 0;
    press->UnitId = Input->UnitId;
    press->MakeCode = Input->MakeCode;
    press->MatchFlags = matchFlags;
    press->LeaseId = driverContext->LeaseId;
    press->Generation = driverContext->ActiveGeneration;
    press->RuleId = rule->RuleId;

    queued = KeyPilotQueueEventLocked(
        driverContext,
        DeviceContext->DeviceHash,
        Input,
        KeyPilotInputDown,
        press->Generation,
        press->RuleId,
        now);
    if (queued && driverContext->LeaseId == press->LeaseId) {
        suppress = press->Suppress;
    }

    WdfSpinLockRelease(driverContext->PolicyLock);
    return suppress;
}

VOID
KeyPilotServiceCallback(
    _In_ PDEVICE_OBJECT DeviceObject,
    _In_ PKEYBOARD_INPUT_DATA InputDataStart,
    _In_ PKEYBOARD_INPUT_DATA InputDataEnd,
    _Inout_ PULONG InputDataConsumed
    )
{
    WDFDEVICE device;
    PKEYPILOT_DEVICE_CONTEXT deviceContext;
    PKEYBOARD_INPUT_DATA current;
    ULONG consumed = 0;

    if (InputDataConsumed == NULL || InputDataStart == NULL || InputDataEnd < InputDataStart) {
        return;
    }

    *InputDataConsumed = 0;
    device = WdfWdmDeviceGetWdfDeviceHandle(DeviceObject);
    if (device == NULL) {
        return;
    }

    deviceContext = KeyPilotGetDeviceContext(device);
    if (deviceContext == NULL || !deviceContext->Connected ||
        deviceContext->UpperConnectData.ClassService == NULL) {
        return;
    }

    for (current = InputDataStart; current < InputDataEnd; ++current) {
        ULONG upperConsumed = 0;

        if (KeyPilotShouldSuppress(deviceContext, current)) {
            ++consumed;
            continue;
        }

        ((PSERVICE_CALLBACK_ROUTINE)(ULONG_PTR)deviceContext->UpperConnectData.ClassService)(
            deviceContext->UpperConnectData.ClassDeviceObject,
            current,
            current + 1,
            &upperConsumed);
        if (upperConsumed != 1) {
            break;
        }
        ++consumed;
    }

    *InputDataConsumed = consumed;
}

static BOOLEAN
KeyPilotForwardRequest(
    _In_ WDFDEVICE Device,
    _In_ WDFREQUEST Request
    )
{
    WDF_REQUEST_SEND_OPTIONS options;

    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(Device), &options)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
        return FALSE;
    }
    return TRUE;
}

VOID
KeyPilotConnectCompletion(
    _In_ WDFREQUEST Request,
    _In_ WDFIOTARGET Target,
    _In_ PWDF_REQUEST_COMPLETION_PARAMS CompletionParams,
    _In_ WDFCONTEXT Context
    )
{
    PKEYPILOT_DEVICE_CONTEXT deviceContext = (PKEYPILOT_DEVICE_CONTEXT)Context;
    NTSTATUS status = CompletionParams->IoStatus.Status;

    UNREFERENCED_PARAMETER(Target);

    if (!NT_SUCCESS(status)) {
        deviceContext->Connected = FALSE;
        RtlZeroMemory(&deviceContext->UpperConnectData, sizeof(deviceContext->UpperConnectData));
    }
    WdfRequestCompleteWithInformation(Request, status, CompletionParams->IoStatus.Information);
}

VOID
KeyPilotEvtIoInternalDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PKEYPILOT_DEVICE_CONTEXT context = KeyPilotGetDeviceContext(device);
    PCONNECT_DATA connectData;
    size_t length;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {
    case IOCTL_INTERNAL_KEYBOARD_CONNECT:
        if (context->Connected) {
            WdfRequestComplete(Request, STATUS_SHARING_VIOLATION);
            return;
        }

        status = WdfRequestRetrieveInputBuffer(Request, sizeof(CONNECT_DATA), (PVOID *)&connectData, &length);
        if (!NT_SUCCESS(status)) {
            WdfRequestComplete(Request, status);
            return;
        }

        context->UpperConnectData = *connectData;
        connectData->ClassDeviceObject = WdfDeviceWdmGetDeviceObject(device);
#pragma warning(disable:4152)
        connectData->ClassService = KeyPilotServiceCallback;
#pragma warning(default:4152)
        context->Connected = TRUE;
        WdfRequestSetCompletionRoutine(Request, KeyPilotConnectCompletion, context);
        if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(device), WDF_NO_SEND_OPTIONS)) {
            context->Connected = FALSE;
            RtlZeroMemory(&context->UpperConnectData, sizeof(context->UpperConnectData));
            WdfRequestComplete(Request, WdfRequestGetStatus(Request));
        }
        return;

    case IOCTL_INTERNAL_KEYBOARD_DISCONNECT:
        /* Kbdclass does not define a safe reconnect sequence for this hook. */
        WdfRequestComplete(Request, STATUS_NOT_IMPLEMENTED);
        return;

    default:
        (VOID)KeyPilotForwardRequest(device, Request);
        return;
    }
}

static BOOLEAN
KeyPilotRulesOverlap(
    _In_ const KEYPILOT_KEYBOARD_RULE *Left,
    _In_ const KEYPILOT_KEYBOARD_RULE *Right
    )
{
    BOOLEAN devicesOverlap;

    devicesOverlap = KeyPilotHashIsZero(Left->DeviceHash) ||
                     KeyPilotHashIsZero(Right->DeviceHash) ||
                     KeyPilotHashesEqual(Left->DeviceHash, Right->DeviceHash);

    return devicesOverlap &&
           Left->MakeCode == Right->MakeCode &&
           (Left->RequiredFlags & Right->IgnoredFlags) == 0 &&
           (Right->RequiredFlags & Left->IgnoredFlags) == 0;
}

static BOOLEAN
KeyPilotRuleMatchesFlags(
    _In_ const KEYPILOT_KEYBOARD_RULE *Rule,
    _In_ USHORT Flags
    )
{
    return (Flags & Rule->RequiredFlags) == Rule->RequiredFlags &&
           (Flags & Rule->IgnoredFlags) == 0;
}

static BOOLEAN
KeyPilotRuleWouldBlockEmergencyKeyboardPath(
    _In_ const KEYPILOT_KEYBOARD_RULE *Rule
    )
{
    if ((Rule->RuleFlags & KeyPilotRuleSuppressOriginal) == 0) {
        return FALSE;
    }

    /* Preserve SAS plus the independent left Ctrl+left Shift+F12 bypass. */
    return ((Rule->MakeCode == 0x001d || Rule->MakeCode == 0x0038) &&
            KeyPilotRuleMatchesFlags(Rule, 0)) ||
           ((Rule->MakeCode == 0x002a || Rule->MakeCode == 0x0058) &&
            KeyPilotRuleMatchesFlags(Rule, 0)) ||
           (Rule->MakeCode == 0x0053 &&
            KeyPilotRuleMatchesFlags(Rule, KeyPilotKeyboardFlagE0));
}

static BOOLEAN
KeyPilotRuleHasExactPrefix(
    _In_ const KEYPILOT_KEYBOARD_RULE *Rule
    )
{
    return (Rule->RequiredFlags == 0 && Rule->IgnoredFlags == KeyPilotKeyboardMatchFlags) ||
           (Rule->RequiredFlags == KeyPilotKeyboardFlagE0 && Rule->IgnoredFlags == KeyPilotKeyboardFlagE1) ||
           (Rule->RequiredFlags == KeyPilotKeyboardFlagE1 && Rule->IgnoredFlags == KeyPilotKeyboardFlagE0);
}

static NTSTATUS
KeyPilotValidateRuleset(
    _In_reads_bytes_(InputLength) const KEYPILOT_RULESET_HEADER *Header,
    _In_ size_t InputLength
    )
{
    size_t expected;
    ULONG left;
    ULONG right;
    const KEYPILOT_KEYBOARD_RULE *rules;

    if (Header == NULL || InputLength < sizeof(*Header) ||
        Header->Header.ProtocolVersion != KEYPILOT_PROTOCOL_VERSION ||
        Header->Header.Size != InputLength ||
        Header->RuleCount > KEYPILOT_MAX_RULES ||
        Header->RuleSize != sizeof(KEYPILOT_KEYBOARD_RULE) ||
        Header->Generation == 0) {
        return STATUS_INVALID_PARAMETER;
    }

    if (Header->RuleCount > (MAXULONG_PTR - sizeof(*Header)) / sizeof(KEYPILOT_KEYBOARD_RULE)) {
        return STATUS_INTEGER_OVERFLOW;
    }
    expected = sizeof(*Header) + ((size_t)Header->RuleCount * sizeof(KEYPILOT_KEYBOARD_RULE));
    if (expected != InputLength) {
        return STATUS_INFO_LENGTH_MISMATCH;
    }

    rules = (const KEYPILOT_KEYBOARD_RULE *)(Header + 1);
    for (left = 0; left < Header->RuleCount; ++left) {
        const KEYPILOT_KEYBOARD_RULE *rule = &rules[left];
        if (rule->MakeCode == 0 || rule->RuleId == 0 || rule->Reserved != 0 ||
            rule->RuleFlags != KeyPilotRuleSuppressOriginal ||
            ((rule->RequiredFlags | rule->IgnoredFlags) & ~KeyPilotKeyboardMatchFlags) != 0 ||
            (rule->RequiredFlags & rule->IgnoredFlags) != 0 ||
            !KeyPilotRuleHasExactPrefix(rule) ||
            KeyPilotRuleWouldBlockEmergencyKeyboardPath(rule)) {
            return STATUS_INVALID_PARAMETER;
        }

        for (right = 0; right < left; ++right) {
            if (rules[right].RuleId == rule->RuleId || KeyPilotRulesOverlap(&rules[right], rule)) {
                return STATUS_OBJECT_NAME_COLLISION;
            }
        }
    }

    return STATUS_SUCCESS;
}

static VOID
KeyPilotCompleteCapabilities(
    _In_ WDFREQUEST Request,
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ size_t OutputBufferLength
    )
{
    PKEYPILOT_CAPABILITIES capabilities;
    NTSTATUS status;
    size_t length;

    if (OutputBufferLength < sizeof(*capabilities)) {
        WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
        return;
    }

    status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*capabilities), (PVOID *)&capabilities, &length);
    if (!NT_SUCCESS(status) || length < sizeof(*capabilities)) {
        WdfRequestComplete(Request, NT_SUCCESS(status) ? STATUS_BUFFER_TOO_SMALL : status);
        return;
    }

    RtlZeroMemory(capabilities, sizeof(*capabilities));
    capabilities->Header.Size = sizeof(*capabilities);
    capabilities->Header.ProtocolVersion = KEYPILOT_PROTOCOL_VERSION;
    capabilities->DriverVersionMajor = KEYPILOT_DRIVER_VERSION_MAJOR;
    capabilities->DriverVersionMinor = KEYPILOT_DRIVER_VERSION_MINOR;
    capabilities->MaximumRules = KEYPILOT_MAX_RULES;

    WdfSpinLockAcquire(Context->PolicyLock);
    capabilities->StateFlags = KeyPilotStateFlagsLocked(Context, KeQueryInterruptTime());
    capabilities->ActiveGeneration = Context->ActiveGeneration;
    WdfSpinLockRelease(Context->PolicyLock);

    WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, sizeof(*capabilities));
}

static VOID
KeyPilotAcquireLease(
    _In_ WDFREQUEST Request,
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ size_t InputBufferLength,
    _In_ size_t OutputBufferLength
    )
{
    PKEYPILOT_LEASE_REQUEST input;
    PKEYPILOT_LEASE_RESPONSE output;
    WDFFILEOBJECT owner = WdfRequestGetFileObject(Request);
    NTSTATUS status;
    size_t length;
    ULONG requested;
    ULONGLONG now;
    ULONGLONG leaseId;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(*input), (PVOID *)&input, &length);
    if (!NT_SUCCESS(status) || length < sizeof(*input) ||
        !KeyPilotHeaderIsExact(&input->Header, sizeof(*input), InputBufferLength) ||
        input->Reserved != 0 ||
        OutputBufferLength < sizeof(*output)) {
        WdfSpinLockAcquire(Context->PolicyLock);
        KeyPilotEnterFailOpenLocked(Context, 0);
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, NT_SUCCESS(status) ? STATUS_INVALID_PARAMETER : status);
        return;
    }

    status = WdfRequestRetrieveOutputBuffer(Request, sizeof(*output), (PVOID *)&output, &length);
    if (!NT_SUCCESS(status) || length < sizeof(*output)) {
        WdfRequestComplete(Request, NT_SUCCESS(status) ? STATUS_BUFFER_TOO_SMALL : status);
        return;
    }

    requested = input->RequestedLeaseMilliseconds;
    if (requested == 0) {
        requested = KEYPILOT_DEFAULT_LEASE_MILLISECONDS;
    }
    if (requested < KEYPILOT_MINIMUM_LEASE_MILLISECONDS || requested > KEYPILOT_MAXIMUM_LEASE_MILLISECONDS) {
        WdfSpinLockAcquire(Context->PolicyLock);
        KeyPilotEnterFailOpenLocked(Context, 0);
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, STATUS_INVALID_PARAMETER);
        return;
    }

    now = KeQueryInterruptTime();
    WdfSpinLockAcquire(Context->PolicyLock);
    (VOID)KeyPilotLeaseIsValidLocked(Context, now);
    if (Context->TrackingLostDeviceCount != 0) {
        KeyPilotEnterFailOpenLocked(Context, KeyPilotDriverInputTrackingLost);
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
        return;
    }
    if (Context->EmergencyBypassDeviceCount != 0) {
        KeyPilotEnterFailOpenLocked(Context, 0);
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, STATUS_DEVICE_NOT_READY);
        return;
    }
    if (Context->LeaseOwner != NULL && Context->LeaseOwner != owner) {
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, STATUS_DEVICE_BUSY);
        return;
    }

    Context->NextLeaseSequence++;
    leaseId = input->ClientNonce ^ now ^ Context->NextLeaseSequence ^ (ULONGLONG)(ULONG_PTR)owner;
    if (leaseId == 0) {
        leaseId = Context->NextLeaseSequence | 1ull;
    }
    Context->LeaseOwner = owner;
    Context->LeaseId = leaseId;
    Context->GrantedLeaseMilliseconds = requested;
    Context->LeaseDeadline100ns = now + ((ULONGLONG)requested * 10000ull);
    Context->ProgressDeadline100ns = 0;
    Context->RuleCount = 0;
    Context->ActiveGeneration = 0;
    Context->EventHead = 0;
    Context->EventCount = 0;
    Context->NextEventSequence = 0;
    Context->LastDeliveredSequence = 0;
    Context->LastAcknowledgedSequence = 0;
    Context->StickyStateFlags = 0;
    WdfSpinLockRelease(Context->PolicyLock);

    RtlZeroMemory(output, sizeof(*output));
    output->Header.Size = sizeof(*output);
    output->Header.ProtocolVersion = KEYPILOT_PROTOCOL_VERSION;
    output->GrantedLeaseMilliseconds = requested;
    output->LeaseId = leaseId;
    WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, sizeof(*output));
}

static VOID
KeyPilotHeartbeat(
    _In_ WDFREQUEST Request,
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ size_t InputBufferLength
    )
{
    PKEYPILOT_HEARTBEAT heartbeat;
    WDFFILEOBJECT owner = WdfRequestGetFileObject(Request);
    NTSTATUS status;
    size_t length;
    ULONGLONG now = KeQueryInterruptTime();

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(*heartbeat), (PVOID *)&heartbeat, &length);
    if (!NT_SUCCESS(status) || length < sizeof(*heartbeat) ||
        !KeyPilotHeaderIsExact(&heartbeat->Header, sizeof(*heartbeat), InputBufferLength)) {
        WdfSpinLockAcquire(Context->PolicyLock);
        KeyPilotEnterFailOpenLocked(Context, 0);
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, NT_SUCCESS(status) ? STATUS_INVALID_PARAMETER : status);
        return;
    }

    WdfSpinLockAcquire(Context->PolicyLock);
    if (!KeyPilotLeaseIsValidLocked(Context, now) || Context->LeaseOwner != owner ||
        Context->LeaseId != heartbeat->LeaseId || Context->ActiveGeneration != heartbeat->ExpectedGeneration ||
        heartbeat->LastAcknowledgedSequence < Context->LastAcknowledgedSequence ||
        heartbeat->LastAcknowledgedSequence > Context->LastDeliveredSequence) {
        KeyPilotEnterFailOpenLocked(Context, 0);
        status = STATUS_ACCESS_DENIED;
    } else {
        if (heartbeat->LastAcknowledgedSequence > Context->LastAcknowledgedSequence) {
            Context->LastAcknowledgedSequence = heartbeat->LastAcknowledgedSequence;
            if (Context->LastAcknowledgedSequence == Context->NextEventSequence) {
                Context->ProgressDeadline100ns = 0;
            } else {
                Context->ProgressDeadline100ns =
                    now + ((ULONGLONG)Context->GrantedLeaseMilliseconds * 10000ull);
            }
        } else if (Context->LastAcknowledgedSequence == Context->NextEventSequence) {
            Context->ProgressDeadline100ns = 0;
        }
        Context->LeaseDeadline100ns = now + ((ULONGLONG)Context->GrantedLeaseMilliseconds * 10000ull);
        status = STATUS_SUCCESS;
    }
    WdfSpinLockRelease(Context->PolicyLock);
    WdfRequestComplete(Request, status);
}

static VOID
KeyPilotReplaceRules(
    _In_ WDFREQUEST Request,
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ size_t InputBufferLength
    )
{
    PKEYPILOT_RULESET_HEADER header;
    const KEYPILOT_KEYBOARD_RULE *rules;
    WDFFILEOBJECT owner = WdfRequestGetFileObject(Request);
    NTSTATUS status;
    size_t length;
    ULONG inactive = 0;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(*header), (PVOID *)&header, &length);
    if (NT_SUCCESS(status) && length >= sizeof(*header)) {
        status = KeyPilotValidateRuleset(header, InputBufferLength);
    } else if (NT_SUCCESS(status)) {
        status = STATUS_BUFFER_TOO_SMALL;
    }
    if (!NT_SUCCESS(status)) {
        WdfSpinLockAcquire(Context->PolicyLock);
        KeyPilotEnterFailOpenLocked(Context, 0);
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, NT_SUCCESS(status) ? STATUS_BUFFER_TOO_SMALL : status);
        return;
    }

    rules = (const KEYPILOT_KEYBOARD_RULE *)(header + 1);
    WdfSpinLockAcquire(Context->PolicyLock);
    if (!KeyPilotLeaseIsValidLocked(Context, KeQueryInterruptTime()) ||
        Context->LeaseOwner != owner || Context->LeaseId != header->LeaseId ||
        header->Generation <= Context->ActiveGeneration) {
        KeyPilotEnterFailOpenLocked(Context, 0);
        status = STATUS_ACCESS_DENIED;
    } else {
        inactive = Context->ActiveRuleBuffer ^ 1u;
        status = STATUS_SUCCESS;
    }
    WdfSpinLockRelease(Context->PolicyLock);
    if (!NT_SUCCESS(status)) {
        WdfRequestComplete(Request, status);
        return;
    }

    /* The control queue is sequential. The inactive buffer is never observed
       by the input path, so the bounded copy does not hold the DISPATCH lock. */
    if (header->RuleCount != 0) {
        RtlCopyMemory(
            Context->RuleBuffers[inactive],
            rules,
            (size_t)header->RuleCount * sizeof(KEYPILOT_KEYBOARD_RULE));
    }

    WdfSpinLockAcquire(Context->PolicyLock);
    if (!KeyPilotLeaseIsValidLocked(Context, KeQueryInterruptTime()) ||
        Context->LeaseOwner != owner || Context->LeaseId != header->LeaseId ||
        header->Generation <= Context->ActiveGeneration ||
        (Context->ActiveRuleBuffer ^ 1u) != inactive) {
        KeyPilotEnterFailOpenLocked(Context, 0);
        status = STATUS_ACCESS_DENIED;
    } else {
        Context->ActiveRuleBuffer = inactive;
        Context->RuleCount = header->RuleCount;
        Context->ActiveGeneration = header->Generation;
        status = STATUS_SUCCESS;
    }
    WdfSpinLockRelease(Context->PolicyLock);
    WdfRequestComplete(Request, status);
}

static VOID
KeyPilotReadEvents(
    _In_ WDFREQUEST Request,
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ size_t OutputBufferLength
    )
{
    PKEYPILOT_INPUT_EVENT output;
    NTSTATUS status;
    size_t length;
    ULONG capacity;
    ULONG count;
    ULONG index;
    ULONGLONG lastDelivered = 0;

    if (OutputBufferLength < sizeof(*output)) {
        WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
        return;
    }
    status = WdfRequestRetrieveOutputBuffer(Request, OutputBufferLength, (PVOID *)&output, &length);
    if (!NT_SUCCESS(status) || length < OutputBufferLength) {
        WdfRequestComplete(Request, NT_SUCCESS(status) ? STATUS_BUFFER_TOO_SMALL : status);
        return;
    }

    capacity = (ULONG)min(OutputBufferLength / sizeof(*output), (size_t)KEYPILOT_MAX_EVENTS_PER_READ);
    WdfSpinLockAcquire(Context->PolicyLock);
    (VOID)KeyPilotLeaseIsValidLocked(Context, KeQueryInterruptTime());
    count = min(capacity, Context->EventCount);
    for (index = 0; index < count; ++index) {
        const KEYPILOT_INPUT_EVENT *source =
            &Context->Events[(Context->EventHead + index) % KEYPILOT_EVENT_CAPACITY];
        /* capacity is derived from the exact WDF output-buffer length above;
           PREfast does not preserve that quotient relationship across WDF. */
#pragma warning(suppress: 6386)
        output[index] = *source;
        lastDelivered = source->Sequence;
    }
    if (count != 0) {
        Context->LastDeliveredSequence = lastDelivered;
    }
    Context->EventHead = (Context->EventHead + count) % KEYPILOT_EVENT_CAPACITY;
    Context->EventCount -= count;
    WdfSpinLockRelease(Context->PolicyLock);

    WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, (size_t)count * sizeof(*output));
}

static VOID
KeyPilotReleaseLease(
    _In_ WDFREQUEST Request,
    _Inout_ PKEYPILOT_DRIVER_CONTEXT Context,
    _In_ size_t InputBufferLength
    )
{
    PKEYPILOT_RELEASE_REQUEST release;
    WDFFILEOBJECT owner = WdfRequestGetFileObject(Request);
    NTSTATUS status;
    size_t length;

    status = WdfRequestRetrieveInputBuffer(Request, sizeof(*release), (PVOID *)&release, &length);
    if (!NT_SUCCESS(status) || length < sizeof(*release) ||
        !KeyPilotHeaderIsExact(&release->Header, sizeof(*release), InputBufferLength)) {
        WdfSpinLockAcquire(Context->PolicyLock);
        KeyPilotEnterFailOpenLocked(Context, 0);
        WdfSpinLockRelease(Context->PolicyLock);
        WdfRequestComplete(Request, NT_SUCCESS(status) ? STATUS_INVALID_PARAMETER : status);
        return;
    }

    WdfSpinLockAcquire(Context->PolicyLock);
    if (Context->LeaseOwner == owner && Context->LeaseId == release->LeaseId) {
        KeyPilotEnterFailOpenLocked(Context, 0);
        status = STATUS_SUCCESS;
    } else {
        KeyPilotEnterFailOpenLocked(Context, 0);
        status = STATUS_ACCESS_DENIED;
    }
    WdfSpinLockRelease(Context->PolicyLock);
    WdfRequestComplete(Request, status);
}

VOID
KeyPilotEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode
    )
{
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    PKEYPILOT_DRIVER_CONTEXT context = KeyPilotGetDriverContext(WdfDeviceGetDriver(device));

    switch (IoControlCode) {
    case IOCTL_KEYPILOT_GET_CAPABILITIES:
        KeyPilotCompleteCapabilities(Request, context, OutputBufferLength);
        break;
    case IOCTL_KEYPILOT_ACQUIRE_LEASE:
        KeyPilotAcquireLease(Request, context, InputBufferLength, OutputBufferLength);
        break;
    case IOCTL_KEYPILOT_HEARTBEAT:
        KeyPilotHeartbeat(Request, context, InputBufferLength);
        break;
    case IOCTL_KEYPILOT_REPLACE_RULES:
        KeyPilotReplaceRules(Request, context, InputBufferLength);
        break;
    case IOCTL_KEYPILOT_READ_EVENTS:
        KeyPilotReadEvents(Request, context, OutputBufferLength);
        break;
    case IOCTL_KEYPILOT_RELEASE_LEASE:
        KeyPilotReleaseLease(Request, context, InputBufferLength);
        break;
    default:
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;
    }
}

VOID
KeyPilotEvtFileCleanup(
    _In_ WDFFILEOBJECT FileObject
    )
{
    WDFDEVICE device = WdfFileObjectGetDevice(FileObject);
    PKEYPILOT_DRIVER_CONTEXT context = KeyPilotGetDriverContext(WdfDeviceGetDriver(device));

    WdfSpinLockAcquire(context->PolicyLock);
    if (context->LeaseOwner == FileObject) {
        KeyPilotEnterFailOpenLocked(context, 0);
    }
    WdfSpinLockRelease(context->PolicyLock);
}

NTSTATUS
KeyPilotEvtDeviceD0Entry(
    _In_ WDFDEVICE Device,
    _In_ WDF_POWER_DEVICE_STATE PreviousState
    )
{
    PKEYPILOT_DEVICE_CONTEXT deviceContext = KeyPilotGetDeviceContext(Device);
    PKEYPILOT_DRIVER_CONTEXT driverContext = deviceContext->DriverContext;

    UNREFERENCED_PARAMETER(PreviousState);

    WdfSpinLockAcquire(driverContext->PolicyLock);
    KeyPilotEnterFailOpenLocked(driverContext, 0);
    KeyPilotRecoverTrackingLocked(deviceContext);
    WdfSpinLockRelease(driverContext->PolicyLock);
    return STATUS_SUCCESS;
}

NTSTATUS
KeyPilotEvtDeviceD0Exit(
    _In_ WDFDEVICE Device,
    _In_ WDF_POWER_DEVICE_STATE TargetState
    )
{
    PKEYPILOT_DEVICE_CONTEXT deviceContext = KeyPilotGetDeviceContext(Device);
    PKEYPILOT_DRIVER_CONTEXT driverContext = deviceContext->DriverContext;

    UNREFERENCED_PARAMETER(TargetState);

    (VOID)WdfTimerStop(deviceContext->EmergencyBypassTimer, TRUE);
    WdfSpinLockAcquire(driverContext->PolicyLock);
    KeyPilotLoseTrackingLocked(deviceContext);
    WdfSpinLockRelease(driverContext->PolicyLock);
    return STATUS_SUCCESS;
}

VOID
KeyPilotEvtDeviceContextCleanup(
    _In_ WDFOBJECT DeviceObject
    )
{
    PKEYPILOT_DEVICE_CONTEXT deviceContext = KeyPilotGetDeviceContext((WDFDEVICE)DeviceObject);
    PKEYPILOT_DRIVER_CONTEXT driverContext = deviceContext->DriverContext;

    if (driverContext != NULL && driverContext->PolicyLock != NULL) {
        if (deviceContext->EmergencyBypassTimer != NULL) {
            (VOID)WdfTimerStop(deviceContext->EmergencyBypassTimer, FALSE);
        }
        WdfSpinLockAcquire(driverContext->PolicyLock);
        KeyPilotEnterFailOpenLocked(driverContext, 0);
        KeyPilotRecoverTrackingLocked(deviceContext);
        WdfSpinLockRelease(driverContext->PolicyLock);
    }
}

static VOID
KeyPilotInitializeDeviceHash(
    _In_ WDFDEVICE Device,
    _Out_writes_bytes_(KEYPILOT_DEVICE_HASH_BYTES) UCHAR *Hash
    )
{
    ULONG required = 0;
    NTSTATUS status;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDFMEMORY memory = NULL;
    WDF_DEVICE_PROPERTY_DATA propertyData;
    DEVPROPTYPE propertyType = DEVPROP_TYPE_EMPTY;
    const UCHAR *bytes = NULL;
    size_t byteCount = 0;
    ULONGLONG first = 14695981039346656037ull;
    ULONGLONG second = 7809847782465536322ull;
    size_t index;

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = Device;
    WDF_DEVICE_PROPERTY_DATA_INIT(&propertyData, &DEVPKEY_Device_InstanceId);
    status = WdfDeviceAllocAndQueryPropertyEx(
        Device,
        &propertyData,
        PagedPool,
        &attributes,
        &memory,
        &propertyType);
    if (NT_SUCCESS(status) && propertyType == DEVPROP_TYPE_STRING) {
        bytes = (const UCHAR *)WdfMemoryGetBuffer(memory, &byteCount);
    } else {
        if (memory != NULL) {
            WdfObjectDelete(memory);
            memory = NULL;
        }
        status = WdfDeviceQueryProperty(Device, DevicePropertyPhysicalDeviceObjectName, 0, NULL, &required);
        if (status == STATUS_BUFFER_TOO_SMALL && required >= sizeof(WCHAR)) {
            status = WdfMemoryCreate(&attributes, PagedPool, 'hPKK', required, &memory, (PVOID *)&bytes);
            if (NT_SUCCESS(status)) {
                status = WdfDeviceQueryProperty(
                    Device,
                    DevicePropertyPhysicalDeviceObjectName,
                    required,
                    (PVOID)bytes,
                    &required);
                byteCount = required;
            }
        }
    }

    if (NT_SUCCESS(status) && bytes != NULL && byteCount != 0) {
        for (index = 0; index < byteCount; ++index) {
            first = (first ^ bytes[index]) * 1099511628211ull;
            second = (second ^ bytes[index]) * 14029467366897019727ull;
        }
    } else {
        first ^= (ULONGLONG)(ULONG_PTR)WdfDeviceWdmGetPhysicalDevice(Device);
        first *= 1099511628211ull;
        second ^= first;
    }

    RtlCopyMemory(Hash, &first, sizeof(first));
    RtlCopyMemory(Hash + sizeof(first), &second, sizeof(second));
    if (memory != NULL) {
        WdfObjectDelete(memory);
    }
}

NTSTATUS
KeyPilotEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit
    )
{
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_PNPPOWER_EVENT_CALLBACKS powerCallbacks;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_TIMER_CONFIG timerConfig;
    WDF_OBJECT_ATTRIBUTES timerAttributes;
    WDFDEVICE device;
    PKEYPILOT_DEVICE_CONTEXT context;
    NTSTATUS status;

    WdfFdoInitSetFilter(DeviceInit);
    WdfDeviceInitSetDeviceType(DeviceInit, FILE_DEVICE_KEYBOARD);
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&powerCallbacks);
    powerCallbacks.EvtDeviceD0Entry = KeyPilotEvtDeviceD0Entry;
    powerCallbacks.EvtDeviceD0Exit = KeyPilotEvtDeviceD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &powerCallbacks);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, KEYPILOT_DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = KeyPilotEvtDeviceContextCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    context = KeyPilotGetDeviceContext(device);
    RtlZeroMemory(context, sizeof(*context));
    context->DriverContext = KeyPilotGetDriverContext(Driver);
    KeyPilotInitializeDeviceHash(device, context->DeviceHash);

    WDF_TIMER_CONFIG_INIT(&timerConfig, KeyPilotEvtEmergencyBypassTimer);
    timerConfig.AutomaticSerialization = FALSE;
    WDF_OBJECT_ATTRIBUTES_INIT(&timerAttributes);
    timerAttributes.ParentObject = device;
    status = WdfTimerCreate(&timerConfig, &timerAttributes, &context->EmergencyBypassTimer);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    /* A lease must not become active until KMDF confirms this stack reached D0. */
    WdfSpinLockAcquire(context->DriverContext->PolicyLock);
    KeyPilotLoseTrackingLocked(context);
    WdfSpinLockRelease(context->DriverContext->PolicyLock);

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoInternalDeviceControl = KeyPilotEvtIoInternalDeviceControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
    return status;
}

static NTSTATUS
KeyPilotCreateControlDevice(
    _In_ WDFDRIVER Driver
    )
{
    PWDFDEVICE_INIT deviceInit = NULL;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_FILEOBJECT_CONFIG fileConfig;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_OBJECT_ATTRIBUTES queueAttributes;
    WDFDEVICE device;
    UNICODE_STRING name;
    UNICODE_STRING link;
    UNICODE_STRING sddl;
    NTSTATUS status;
    PKEYPILOT_DRIVER_CONTEXT context = KeyPilotGetDriverContext(Driver);

    RtlInitUnicodeString(&sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");
    deviceInit = WdfControlDeviceInitAllocate(Driver, &sddl);
    if (deviceInit == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    WdfDeviceInitSetExclusive(deviceInit, TRUE);
    WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, KeyPilotEvtFileCleanup);
    WdfDeviceInitSetFileObjectConfig(deviceInit, &fileConfig, WDF_NO_OBJECT_ATTRIBUTES);

    RtlInitUnicodeString(&name, KEYPILOT_NT_DEVICE_NAME);
    status = WdfDeviceInitAssignName(deviceInit, &name);
    if (!NT_SUCCESS(status)) {
        WdfDeviceInitFree(deviceInit);
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    status = WdfDeviceCreate(&deviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    RtlInitUnicodeString(&link, KEYPILOT_DOS_DEVICE_NAME);
    status = WdfDeviceCreateSymbolicLink(device, &link);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(device);
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = KeyPilotEvtIoDeviceControl;
    WDF_OBJECT_ATTRIBUTES_INIT(&queueAttributes);
    queueAttributes.ExecutionLevel = WdfExecutionLevelPassive;
    status = WdfIoQueueCreate(device, &queueConfig, &queueAttributes, WDF_NO_HANDLE);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(device);
        return status;
    }

    context->ControlDevice = device;
    WdfControlFinishInitializing(device);
    return STATUS_SUCCESS;
}

VOID
KeyPilotEvtDriverUnload(
    _In_ WDFDRIVER Driver
    )
{
    PKEYPILOT_DRIVER_CONTEXT context = KeyPilotGetDriverContext(Driver);

    if (context->PolicyLock != NULL) {
        WdfSpinLockAcquire(context->PolicyLock);
        KeyPilotEnterFailOpenLocked(context, 0);
        WdfSpinLockRelease(context->PolicyLock);
    }
}

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath
    )
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES lockAttributes;
    WDFDRIVER driver;
    PKEYPILOT_DRIVER_CONTEXT context;
    NTSTATUS status;

    WDF_DRIVER_CONFIG_INIT(&config, KeyPilotEvtDeviceAdd);
    config.EvtDriverUnload = KeyPilotEvtDriverUnload;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, KEYPILOT_DRIVER_CONTEXT);

    status = WdfDriverCreate(DriverObject, RegistryPath, &attributes, &config, &driver);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    context = KeyPilotGetDriverContext(driver);
    RtlZeroMemory(context, sizeof(*context));
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = driver;
    status = WdfSpinLockCreate(&lockAttributes, &context->PolicyLock);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = KeyPilotCreateControlDevice(driver);
    if (!NT_SUCCESS(status)) {
        /* The input filter can still load as a pure pass-through device. */
        context->ControlDevice = NULL;
    }
    return STATUS_SUCCESS;
}
