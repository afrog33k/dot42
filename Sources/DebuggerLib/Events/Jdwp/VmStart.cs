﻿namespace Dot42.DebuggerLib.Events.Jdwp
{
    /// <summary>
    /// Notification of initialization of a target VM. This event is received before the main thread is started and before any application code has been executed. 
    /// Before this event occurs a significant amount of system code has executed and a number of system classes have been loaded. This event is always 
    /// generated by the target VM, even if not explicitly requested. 
    /// </summary>
    public sealed class VmStart : BaseEvent
    {
        public VmStart(JdwpPacket.DataReaderWriter reader)
            : base(reader)
        {
        }

        /// <summary>
        /// Accept a visit by the given visitor.
        /// </summary>
        public override TResult Accept<TResult, TData>(EventVisitor<TResult, TData> visitor, TData data)
        {
            return visitor.Visit(this, data);
        }
    }
}
