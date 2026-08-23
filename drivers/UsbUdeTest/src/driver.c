#include <ntddk.h>
#include <wdf.h>
#include <ntstrsafe.h>
#include <usb.h>
#include <wdfusb.h>
#include <initguid.h>
#include <UdeCx.h>

#define USB_UDE_POOL_TAG 'UrdE'
#define USB_UDE_SERIAL_CHARS 64
#define IOCTL_USB_UDE_ATTACH CTL_CODE(FILE_DEVICE_UNKNOWN, 0x800, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_USB_UDE_DETACH CTL_CODE(FILE_DEVICE_UNKNOWN, 0x801, METHOD_BUFFERED, FILE_WRITE_DATA)
#define IOCTL_USB_UDE_QUERY  CTL_CODE(FILE_DEVICE_UNKNOWN, 0x802, METHOD_BUFFERED, FILE_READ_DATA)

DEFINE_GUID(GUID_DEVINTERFACE_USB_UDE_TEST,
    0x77dc40f2, 0x80fb, 0x4f86, 0xa6, 0xd4, 0x79, 0x3a, 0xb5, 0x6d, 0x2d, 0x45);

typedef struct _USB_UDE_ATTACH_REQUEST {
    WCHAR Serial[USB_UDE_SERIAL_CHARS];
} USB_UDE_ATTACH_REQUEST, *PUSB_UDE_ATTACH_REQUEST;

typedef struct _USB_UDE_STATUS {
    ULONG Attached;
    WCHAR Serial[USB_UDE_SERIAL_CHARS];
} USB_UDE_STATUS, *PUSB_UDE_STATUS;

typedef struct _DEVICE_CONTEXT {
    UDECXUSBDEVICE UsbDevice;
    UDECXUSBENDPOINT ControlEndpoint;
    WDFQUEUE ControlQueue;
    BOOLEAN Attached;
    WCHAR Serial[USB_UDE_SERIAL_CHARS];
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, DeviceGetContext);

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD UsbUdeEvtDeviceAdd;
EVT_WDF_OBJECT_CONTEXT_CLEANUP UsbUdeEvtDeviceCleanup;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL UsbUdeEvtIoDeviceControl;
EVT_WDF_IO_QUEUE_IO_INTERNAL_DEVICE_CONTROL UsbUdeEvtIoInternalDeviceControl;
EVT_UDECX_USB_ENDPOINT_RESET UsbUdeEvtEndpointReset;
EVT_UDECX_WDF_DEVICE_QUERY_USB_CAPABILITY UsbUdeEvtQueryUsbCapability;

static const USB_DEVICE_DESCRIPTOR UsbDeviceDescriptor = {
    sizeof(USB_DEVICE_DESCRIPTOR),
    USB_DEVICE_DESCRIPTOR_TYPE,
    0x0200,
    0x00,
    0x00,
    0x00,
    64,
    0xED1D,
    0x0001,
    0x0100,
    1,
    2,
    3,
    1
};

static const UCHAR UsbConfigurationDescriptor[] = {
    0x09, USB_CONFIGURATION_DESCRIPTOR_TYPE,
    0x12, 0x00,
    0x01,
    0x01,
    0x00,
    0x80,
    0x32,
    0x09, USB_INTERFACE_DESCRIPTOR_TYPE,
    0x00,
    0x00,
    0x00,
    0xFF,
    0x00,
    0x00,
    0x00
};

static const UCHAR UsbLanguageDescriptor[] = {
    0x04, USB_STRING_DESCRIPTOR_TYPE, 0x09, 0x04
};

static BOOLEAN UsbUdeValidateSerial(_In_reads_(USB_UDE_SERIAL_CHARS) const WCHAR* Serial)
{
    size_t index;
    const WCHAR prefix[] = L"EDR_USB_";

    for (index = 0; index < RTL_NUMBER_OF(prefix) - 1; index++) {
        if (Serial[index] != prefix[index]) {
            return FALSE;
        }
    }

    for (; index < USB_UDE_SERIAL_CHARS; index++) {
        WCHAR value = Serial[index];
        if (value == L'\0') {
            return index > RTL_NUMBER_OF(prefix) - 1;
        }
        if (!((value >= L'0' && value <= L'9') ||
              (value >= L'A' && value <= L'Z') ||
              value == L'_' || value == L'-')) {
            return FALSE;
        }
    }
    return FALSE;
}

static VOID UsbUdeClearState(_Inout_ PDEVICE_CONTEXT Context)
{
    Context->UsbDevice = NULL;
    Context->ControlEndpoint = NULL;
    Context->Attached = FALSE;
    RtlZeroMemory(Context->Serial, sizeof(Context->Serial));
}

static NTSTATUS UsbUdeCreateControlEndpoint(
    _In_ PDEVICE_CONTEXT Context,
    _In_ UDECXUSBDEVICE UsbDevice)
{
    NTSTATUS status;
    PUDECXUSBENDPOINT_INIT endpointInit;
    UDECX_USB_ENDPOINT_CALLBACKS callbacks;
    UDECXUSBENDPOINT endpoint;

    endpointInit = UdecxUsbSimpleEndpointInitAllocate(UsbDevice);
    if (endpointInit == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    UdecxUsbEndpointInitSetEndpointAddress(endpointInit, USB_DEFAULT_ENDPOINT_ADDRESS);
    UDECX_USB_ENDPOINT_CALLBACKS_INIT(&callbacks, UsbUdeEvtEndpointReset);
    UdecxUsbEndpointInitSetCallbacks(endpointInit, &callbacks);
    status = UdecxUsbEndpointCreate(&endpointInit, WDF_NO_OBJECT_ATTRIBUTES, &endpoint);
    if (!NT_SUCCESS(status)) {
        if (endpointInit != NULL) {
            UdecxUsbEndpointInitFree(endpointInit);
        }
        return status;
    }

    UdecxUsbEndpointSetWdfIoQueue(endpoint, Context->ControlQueue);
    Context->ControlEndpoint = endpoint;
    return STATUS_SUCCESS;
}

static NTSTATUS UsbUdeAttach(
    _In_ WDFDEVICE Device,
    _In_reads_(USB_UDE_SERIAL_CHARS) const WCHAR* Serial)
{
    NTSTATUS status;
    PDEVICE_CONTEXT context;
    PUDECXUSBDEVICE_INIT deviceInit;
    UDECXUSBDEVICE usbDevice;
    UDECX_USB_DEVICE_PLUG_IN_OPTIONS plugInOptions;
    UNICODE_STRING manufacturer;
    UNICODE_STRING product;
    UNICODE_STRING serialString;

    context = DeviceGetContext(Device);
    if (context->Attached || context->UsbDevice != NULL) {
        return STATUS_DEVICE_BUSY;
    }
    if (!UsbUdeValidateSerial(Serial)) {
        return STATUS_INVALID_PARAMETER;
    }

    deviceInit = UdecxUsbDeviceInitAllocate(Device);
    if (deviceInit == NULL) {
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    UdecxUsbDeviceInitSetSpeed(deviceInit, UdecxUsbFullSpeed);
    UdecxUsbDeviceInitSetEndpointsType(deviceInit, UdecxEndpointTypeSimple);

    status = UdecxUsbDeviceInitAddDescriptor(deviceInit, (PUCHAR)&UsbDeviceDescriptor,
        (USHORT)sizeof(UsbDeviceDescriptor));
    if (!NT_SUCCESS(status)) {
        goto FreeInit;
    }
    status = UdecxUsbDeviceInitAddDescriptor(deviceInit, (PUCHAR)UsbConfigurationDescriptor,
        (USHORT)sizeof(UsbConfigurationDescriptor));
    if (!NT_SUCCESS(status)) {
        goto FreeInit;
    }
    status = UdecxUsbDeviceInitAddDescriptorWithIndex(deviceInit, (PUCHAR)UsbLanguageDescriptor,
        (USHORT)sizeof(UsbLanguageDescriptor), 0);
    if (!NT_SUCCESS(status)) {
        goto FreeInit;
    }

    RtlInitUnicodeString(&manufacturer, L"Tencent EDR Test");
    RtlInitUnicodeString(&product, L"EDR USB Telemetry Device");
    RtlInitUnicodeString(&serialString, Serial);
    status = UdecxUsbDeviceInitAddStringDescriptor(deviceInit, &manufacturer, 1, 0x0409);
    if (!NT_SUCCESS(status)) {
        goto FreeInit;
    }
    status = UdecxUsbDeviceInitAddStringDescriptor(deviceInit, &product, 2, 0x0409);
    if (!NT_SUCCESS(status)) {
        goto FreeInit;
    }
    status = UdecxUsbDeviceInitAddStringDescriptor(deviceInit, &serialString, 3, 0x0409);
    if (!NT_SUCCESS(status)) {
        goto FreeInit;
    }

    usbDevice = NULL;
    status = UdecxUsbDeviceCreate(&deviceInit, WDF_NO_OBJECT_ATTRIBUTES, &usbDevice);
    if (!NT_SUCCESS(status)) {
        goto FreeInit;
    }

    context->UsbDevice = usbDevice;
    status = UsbUdeCreateControlEndpoint(context, usbDevice);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(usbDevice);
        UsbUdeClearState(context);
        return status;
    }

    UDECX_USB_DEVICE_PLUG_IN_OPTIONS_INIT(&plugInOptions);
    plugInOptions.Usb20PortNumber = 1;
    status = UdecxUsbDevicePlugIn(usbDevice, &plugInOptions);
    if (!NT_SUCCESS(status)) {
        WdfObjectDelete(usbDevice);
        UsbUdeClearState(context);
        return status;
    }

    context->Attached = TRUE;
    RtlStringCchCopyW(context->Serial, USB_UDE_SERIAL_CHARS, Serial);
    return STATUS_SUCCESS;

FreeInit:
    UdecxUsbDeviceInitFree(deviceInit);
    return status;
}

static NTSTATUS UsbUdeDetach(_In_ WDFDEVICE Device)
{
    NTSTATUS status;
    PDEVICE_CONTEXT context;

    context = DeviceGetContext(Device);
    if (!context->Attached || context->UsbDevice == NULL) {
        return STATUS_DEVICE_NOT_CONNECTED;
    }

    status = UdecxUsbDevicePlugOutAndDelete(context->UsbDevice);
    if (NT_SUCCESS(status)) {
        UsbUdeClearState(context);
    }
    return status;
}

NTSTATUS DriverEntry(
    _In_ PDRIVER_OBJECT DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;

    WDF_DRIVER_CONFIG_INIT(&config, UsbUdeEvtDeviceAdd);
    config.DriverPoolTag = USB_UDE_POOL_TAG;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    return WdfDriverCreate(DriverObject, RegistryPath, &attributes, &config, WDF_NO_HANDLE);
}

NTSTATUS UsbUdeEvtDeviceAdd(
    _In_ WDFDRIVER Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    UDECX_WDF_DEVICE_CONFIG udeConfig;
    PDEVICE_CONTEXT context;
    DECLARE_CONST_UNICODE_STRING(sddl, L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");

    UNREFERENCED_PARAMETER(Driver);

    // WdfDeviceInitAssignSDDLString cannot secure an unnamed device object.
    // Let KMDF assign a collision-free name before applying the restricted SDDL.
    WdfDeviceInitSetCharacteristics(DeviceInit, FILE_AUTOGENERATED_DEVICE_NAME, FALSE);
    status = WdfDeviceInitAssignSDDLString(DeviceInit, &sddl);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    status = UdecxInitializeWdfDeviceInit(DeviceInit);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = UsbUdeEvtDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    context = DeviceGetContext(device);
    UsbUdeClearState(context);
    context->ControlQueue = NULL;

    UDECX_WDF_DEVICE_CONFIG_INIT(&udeConfig, UsbUdeEvtQueryUsbCapability);
    udeConfig.NumberOfUsb20Ports = 1;
    udeConfig.NumberOfUsb30Ports = 0;
    status = UdecxWdfDeviceAddUsbDeviceEmulation(device, &udeConfig);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_USB_UDE_TEST, NULL);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoInternalDeviceControl = UsbUdeEvtIoInternalDeviceControl;
    queueConfig.PowerManaged = WdfFalse;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &context->ControlQueue);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchSequential);
    queueConfig.EvtIoDeviceControl = UsbUdeEvtIoDeviceControl;
    queueConfig.PowerManaged = WdfFalse;
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, WDF_NO_HANDLE);
}

VOID UsbUdeEvtDeviceCleanup(_In_ WDFOBJECT Object)
{
    PDEVICE_CONTEXT context;

    context = DeviceGetContext((WDFDEVICE)Object);
    if (context->Attached && context->UsbDevice != NULL) {
        (VOID)UdecxUsbDevicePlugOutAndDelete(context->UsbDevice);
        UsbUdeClearState(context);
    }
}

VOID UsbUdeEvtIoDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    NTSTATUS status;
    WDFDEVICE device;
    PDEVICE_CONTEXT context;
    PUSB_UDE_ATTACH_REQUEST attachRequest;
    PUSB_UDE_STATUS queryResult;
    size_t bufferLength;
    size_t information;

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);
    information = 0;
    device = WdfIoQueueGetDevice(Queue);
    context = DeviceGetContext(device);

    switch (IoControlCode) {
    case IOCTL_USB_UDE_ATTACH:
        status = WdfRequestRetrieveInputBuffer(Request, sizeof(USB_UDE_ATTACH_REQUEST),
            (PVOID*)&attachRequest, &bufferLength);
        if (NT_SUCCESS(status)) {
            status = UsbUdeAttach(device, attachRequest->Serial);
        }
        break;
    case IOCTL_USB_UDE_DETACH:
        status = UsbUdeDetach(device);
        break;
    case IOCTL_USB_UDE_QUERY:
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(USB_UDE_STATUS),
            (PVOID*)&queryResult, &bufferLength);
        if (NT_SUCCESS(status)) {
            RtlZeroMemory(queryResult, sizeof(*queryResult));
            queryResult->Attached = context->Attached ? 1UL : 0UL;
            RtlStringCchCopyW(queryResult->Serial, USB_UDE_SERIAL_CHARS, context->Serial);
            information = sizeof(*queryResult);
        }
        break;
    default:
        if (UdecxWdfDeviceTryHandleUserIoctl(device, Request)) {
            return;
        }
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    WdfRequestCompleteWithInformation(Request, status, information);
}

VOID UsbUdeEvtIoInternalDeviceControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);
    UNREFERENCED_PARAMETER(IoControlCode);
    UdecxUrbCompleteWithNtStatus(Request, STATUS_NOT_SUPPORTED);
}

VOID UsbUdeEvtEndpointReset(
    _In_ UDECXUSBENDPOINT UdecxUsbEndpoint,
    _In_ WDFREQUEST Request)
{
    UNREFERENCED_PARAMETER(UdecxUsbEndpoint);
    WdfRequestComplete(Request, STATUS_SUCCESS);
}

NTSTATUS UsbUdeEvtQueryUsbCapability(
    _In_ WDFDEVICE UdecxWdfDevice,
    _In_ PGUID CapabilityType,
    _In_ ULONG OutputBufferLength,
    _Out_writes_to_opt_(OutputBufferLength, *ResultLength) PVOID OutputBuffer,
    _Out_ PULONG ResultLength)
{
    UNREFERENCED_PARAMETER(UdecxWdfDevice);
    UNREFERENCED_PARAMETER(CapabilityType);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(OutputBuffer);
    *ResultLength = 0;
    return STATUS_NOT_SUPPORTED;
}
