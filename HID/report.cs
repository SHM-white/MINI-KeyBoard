using System;

namespace HID;

// Used only in mode 0
public class report : EventArgs
{
	public readonly byte reportID;

	public readonly byte[] reportBuff;

	public report(byte id, byte[] arrayBuff)
	{
		reportID = id;
		reportBuff = arrayBuff;
	}
}
