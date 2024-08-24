using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HID;

//This class may be zhe origin interface of write
// HID设备操作类
public class Hid
{
    // 操作结果枚举
    public enum HID_RETURN
    {
        SUCCESS,
        NO_DEVICE_CONECTED,
        DEVICE_NOT_FIND,
        DEVICE_OPENED,
        WRITE_FAILD,
        READ_FAILD
    }

    // 设备接口缓冲区结构体
    [StructLayout(LayoutKind.Explicit)]
    private struct DevBroadcastDeviceInterfaceBuffer
    {
        [FieldOffset(0)]
        public int dbch_size;

        [FieldOffset(4)]
        public int dbch_devicetype;

        [FieldOffset(8)]
        public int dbch_reserved;

        public DevBroadcastDeviceInterfaceBuffer(int deviceType)
        {
            dbch_size = Marshal.SizeOf(typeof(DevBroadcastDeviceInterfaceBuffer));
            dbch_devicetype = deviceType;
            dbch_reserved = 0;
        }
    }

    // 常量定义
    public const uint GENERIC_READ = 2147483648u;
    public const uint GENERIC_WRITE = 1073741824u;
    public const uint FILE_SHARE_READ = 1u;
    public const uint FILE_SHARE_WRITE = 2u;
    public const int OPEN_EXISTING = 3;

    private static IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    private const int MAX_USB_DEVICES = 64;

    private bool deviceOpened;
    private FileStream hidDevice;
    private IAsyncResult readResult;
    private int outputReportLength;
    private int inputReportLength;

    // 属性定义
    public int OutputReportLength => outputReportLength;
    public int InputReportLength => inputReportLength;
    public bool Opened => deviceOpened;

    // 事件定义
    public event EventHandler<report> DataReceived;
    public event EventHandler DeviceRemoved;

    // 获取设备序列号列表
    public static HID_RETURN GetDeviceSerialList(ushort vID, ushort pID, ref List<string> serialList)
    {
        serialList.Clear();
        List<string> deviceList = new List<string>();
        GetHidDeviceList(ref deviceList);
        if (deviceList.Count == 0)
        {
            return HID_RETURN.NO_DEVICE_CONECTED;
        }
        for (int i = 0; i < deviceList.Count; i++)
        {
            IntPtr intPtr = CreateFile(deviceList[i], 3221225472u, 0u, 0u, 3u, 1073741824u, 0u);
            if (intPtr != INVALID_HANDLE_VALUE)
            {
                IntPtr intPtr2 = Marshal.AllocHGlobal(512);
                HidD_GetAttributes(intPtr, out var attributes);
                HidD_GetSerialNumberString(intPtr, intPtr2, 512);
                string item = Marshal.PtrToStringAuto(intPtr2);
                Marshal.FreeHGlobal(intPtr2);
                if (attributes.VendorID == vID && attributes.ProductID == pID)
                {
                    serialList.Add(item);
                }
                CloseHandle(intPtr);
            }
        }
        return HID_RETURN.SUCCESS;
    }

    // 打开设备
    public IntPtr OpenDevice(ushort vID, ushort pID)
    {
        if (!deviceOpened)
        {
            List<string> deviceList = new List<string>();
            GetHidDeviceList(ref deviceList);
            if (deviceList.Count == 0)
            {
                return INVALID_HANDLE_VALUE;
            }
            for (int i = 0; i < deviceList.Count; i++)
            {
                IntPtr intPtr = CreateFile(deviceList[i], 3221225472u, 0u, 0u, 3u, 1073741824u, 0u);
                if (intPtr != INVALID_HANDLE_VALUE)
                {
                    IntPtr intPtr2 = Marshal.AllocHGlobal(512);
                    if (!HidD_GetAttributes(intPtr, out var attributes))
                    {
                        CloseHandle(intPtr);
                        return INVALID_HANDLE_VALUE;
                    }
                    HidD_GetSerialNumberString(intPtr, intPtr2, 512);
                    Marshal.PtrToStringAuto(intPtr2);
                    Marshal.FreeHGlobal(intPtr2);
                    if (attributes.VendorID == vID && attributes.ProductID == pID)
                    {
                        HidD_GetPreparsedData(intPtr, out var PreparsedData);
                        HidP_GetCaps(PreparsedData, out var Capabilities);
                        HidD_FreePreparsedData(PreparsedData);
                        outputReportLength = Capabilities.OutputReportByteLength;
                        inputReportLength = Capabilities.InputReportByteLength;
                        hidDevice = new FileStream(new SafeFileHandle(intPtr, ownsHandle: false), FileAccess.ReadWrite, inputReportLength, isAsync: true);
                        deviceOpened = true;
                        BeginAsyncRead();
                        return intPtr;
                    }
                }
                CloseHandle(intPtr);
            }
            return INVALID_HANDLE_VALUE;
        }
        return INVALID_HANDLE_VALUE;
    }

    // 关闭设备
    public void CloseDevice(IntPtr device)
    {
        if (deviceOpened)
        {
            deviceOpened = false;
            hidDevice.Close();
            CloseHandle(device);
        }
    }

    // 开始异步读取
    private void BeginAsyncRead()
    {
        byte[] array = new byte[InputReportLength];
        _ = hidDevice.Handle;
        readResult = hidDevice.BeginRead(array, 0, InputReportLength, ReadCompleted, array);
    }

    // 读取完成回调
    private void ReadCompleted(IAsyncResult iResult)
    {
        byte[] array = (byte[])iResult.AsyncState;
        try
        {
            if (deviceOpened)
            {
                hidDevice.EndRead(iResult);
                byte[] array2 = new byte[array.Length - 1];
                for (int i = 1; i < array.Length; i++)
                {
                    array2[i - 1] = array[i];
                }
                report e = new report(array[0], array2);
                OnDataReceived(e);
                BeginAsyncRead();
            }
        }
        catch (IOException)
        {
            EventArgs e2 = new EventArgs();
            OnDeviceRemoved(e2);
        }
    }

    // 数据接收事件处理
    protected virtual void OnDataReceived(report e)
    {
        if (this.DataReceived != null)
        {
            this.DataReceived(this, e);
        }
    }

    // 设备移除事件处理
    protected virtual void OnDeviceRemoved(EventArgs e)
    {
        deviceOpened = false;
        if (this.DeviceRemoved != null)
        {
            this.DeviceRemoved(this, e);
        }
    }

    // 写入数据
    // 该方法用于向设备写入报告数据。如果设备已打开，则尝试将报告数据写入设备。
    // 如果写入成功，返回HID_RETURN.SUCCESS；如果设备未打开或写入失败，返回HID_RETURN.WRITE_FAILD。
    public HID_RETURN Write(report r)
    {
        if (deviceOpened)
        {
            try
            {
                byte[] array = new byte[outputReportLength];
                array[0] = r.reportID;
                int num = 0;
                num = ((r.reportBuff.Length >= outputReportLength - 1) ? (outputReportLength - 1) : r.reportBuff.Length);
                for (int i = 1; i <= num; i++)
                {
                    array[i] = r.reportBuff[i - 1];
                }
                hidDevice.Write(array, 0, 65);
                return HID_RETURN.SUCCESS;
            }
            catch
            {
            }
        }
        return HID_RETURN.WRITE_FAILD;
    }

    // 获取HID设备列表
    public static void GetHidDeviceList(ref List<string> deviceList)
    {
        Guid HidGuid = Guid.Empty;
        uint num = 0u;
        deviceList.Clear();
        HidD_GetHidGuid(ref HidGuid);
        IntPtr intPtr = SetupDiGetClassDevs(ref HidGuid, 0u, IntPtr.Zero, (DIGCF)18);
        if (intPtr != IntPtr.Zero)
        {
            SP_DEVICE_INTERFACE_DATA deviceInterfaceData = default(SP_DEVICE_INTERFACE_DATA);
            deviceInterfaceData.cbSize = Marshal.SizeOf((object)deviceInterfaceData);
            for (num = 0u; num < 64; num++)
            {
                if (SetupDiEnumDeviceInterfaces(intPtr, IntPtr.Zero, ref HidGuid, num, ref deviceInterfaceData))
                {
                    int requiredSize = 0;
                    SetupDiGetDeviceInterfaceDetail(intPtr, ref deviceInterfaceData, IntPtr.Zero, requiredSize, ref requiredSize, null);
                    IntPtr intPtr2 = Marshal.AllocHGlobal(requiredSize);
                    SP_DEVICE_INTERFACE_DETAIL_DATA sP_DEVICE_INTERFACE_DETAIL_DATA = default(SP_DEVICE_INTERFACE_DETAIL_DATA);
                    sP_DEVICE_INTERFACE_DETAIL_DATA.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DETAIL_DATA));
                    Marshal.StructureToPtr((object)sP_DEVICE_INTERFACE_DETAIL_DATA, intPtr2, fDeleteOld: false);
                    if (SetupDiGetDeviceInterfaceDetail(intPtr, ref deviceInterfaceData, intPtr2, requiredSize, ref requiredSize, null))
                    {
                        deviceList.Add(Marshal.PtrToStringAuto((IntPtr)((int)intPtr2 + 4)));
                    }
                    Marshal.FreeHGlobal(intPtr2);
                }
            }
        }
        SetupDiDestroyDeviceInfoList(intPtr);
    }

    // 获取HID GUID
    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(ref Guid HidGuid);

    // 获取设备类信息
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, uint Enumerator, IntPtr HwndParent, DIGCF Flags);

    // 销毁设备信息列表
    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // 枚举设备接口
    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    // 获取设备接口详情
    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, SP_DEVINFO_DATA deviceInfoData);

    // 获取设备属性
    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr hidDeviceObject, out HIDD_ATTRIBUTES attributes);

    // 获取设备序列号字符串
    [DllImport("hid.dll")]
    private static extern bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, IntPtr buffer, int bufferLength);

    // 获取预解析数据
    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr PreparsedData);

    // 释放预解析数据
    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    // 获取设备能力
    [DllImport("hid.dll")]
    private static extern uint HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    // 创建文件句柄
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, uint securityAttributes, uint creationDisposition, uint flagsAndAttributes, uint templateFile);

    // 关闭句柄
    [DllImport("kernel32.dll")]
    private static extern int CloseHandle(IntPtr hObject);

    // 读取文件
    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, byte[] buffer, uint numberOfBytesToRead, out uint numberOfBytesRead, IntPtr lpOverlapped);

    // 写入文件
    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, IntPtr lpOverlapped);

    // 注册设备通知
    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr notificationFilter, int flags);

    // 注册HID通知
    public static IntPtr RegisterHIDNotification(IntPtr recipient)
    {
        IntPtr intPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(DevBroadcastDeviceInterfaceBuffer)));
        Marshal.StructureToPtr((object)new DevBroadcastDeviceInterfaceBuffer(5), intPtr, fDeleteOld: false);
        return RegisterDeviceNotification(recipient, intPtr, 0);
    }

    // 注销设备通知
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    // 注销HID通知
    public static bool UnRegisterHIDNotification(IntPtr hDEVNotify)
    {
        return UnregisterDeviceNotification(hDEVNotify);
    }
}
