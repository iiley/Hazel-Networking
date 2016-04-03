﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hazel
{
    public class UdpClientConnection : UdpConnection
    {
        /// <summary>
        ///     The socket we're connected via.
        /// </summary>
        Socket socket;

        /// <summary>
        ///     The buffer to store incomming data in.
        /// </summary>
        byte[] dataBuffer = new byte[ushort.MaxValue];

        /// <summary>
        ///     Creates a new UdpClientConnection.
        /// </summary>
        public UdpClientConnection()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        /// <summary>
        ///     Writes an array of bytes to the connection.
        /// </summary>
        /// <param name="bytes">The bytes of the message to send.</param>
        /// <param name="sendOption">The option this data is requested to send with.</param>
        public override void WriteBytes(byte[] bytes, SendOption sendOption = SendOption.None)
        {
            //Add sendflag byte to start
            byte[] fullBytes = new byte[bytes.Length + 1];
            fullBytes[0] = (byte)sendOption;
            Buffer.BlockCopy(bytes, 0, fullBytes, 1, bytes.Length);

            //Pack
            SocketAsyncEventArgs args = new SocketAsyncEventArgs();
            args.SetBuffer(fullBytes, 0, fullBytes.Length);
            args.RemoteEndPoint = RemoteEndPoint;

            lock (socket)
            {
                if (State != ConnectionState.Connected)
                    throw new InvalidOperationException("Could not send data as this Connection is not connected. Did you disconnect?");

                try
                {
                    socket.SendToAsync(args);
                }
                catch (ObjectDisposedException)
                {
                    //User probably called Disconnect in between this method starting and here so report the issue
                    throw new InvalidOperationException("Could not send data as this Connection is not connected. Did you disconnect?");
                }
                catch (SocketException e)
                {
                    HazelException he = new HazelException("Could not send data as a SocketException occured.", e);
                    HandleDisconnect(he);
                    throw he;
                }
            }

            Statistics.LogSend(bytes.Length, fullBytes.Length);
        }

        /// <summary>
        ///     Connects this Connection to a given remote server and begins listening for data.
        /// </summary>
        public override void Connect(ConnectionEndPoint remoteEndPoint)
        {
            NetworkEndPoint nep = remoteEndPoint as NetworkEndPoint;
            if (nep == null)
            {
                throw new ArgumentException("The remote end point of a TCP connection must be a NetworkEndPoint.");
            }

            this.EndPoint = nep;
            this.RemoteEndPoint = nep.EndPoint;

            lock (socket)
            {
                if (State != ConnectionState.NotConnected)
                    throw new InvalidOperationException("Cannot connect as the Connection is already connected.");

                State = ConnectionState.Connecting;

                //Begin listening
                try
                {
                    //TODO should that really be IPAddress.Any?
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                }
                catch (SocketException e)
                {
                    throw new HazelException("A socket exception occured while binding to the port.", e);
                }

                try
                {
                    StartListeningForData();
                }
                catch (ObjectDisposedException)
                {
                    throw new HazelException("Could not begin read as the socket has been disposed of, did you disconnect?");
                }
                catch (SocketException e)
                {
                    throw new HazelException("A Socket exception occured while initiating a receive operation.", e);
                }

                State = ConnectionState.Connected;
            }

            //Write bytes to the server to tell it hi (and to punch a hole in our NAT, if present).
            WriteBytes(new byte[] { 0 }, SendOption.Reliable);
        }

        /// <summary>
        ///     Instructs the listener to begin listening.
        /// </summary>
        void StartListeningForData()
        {
            socket.BeginReceive(dataBuffer, 0, dataBuffer.Length, SocketFlags.None, ReadCallback, dataBuffer);
        }

        /// <summary>
        ///     Called when data has been received by the socket.
        /// </summary>
        /// <param name="result">The asyncronous operation's result.</param>
        void ReadCallback(IAsyncResult result)
        {
            int bytesReceived;

            //End the receive operation
            try
            {
                lock (socket)
                    bytesReceived = socket.EndReceive(result);
            }
            catch (ObjectDisposedException)
            {
                //If the socket's been disposed then we can just end there.
                return;
            }
            catch (SocketException e)
            {
                HandleDisconnect(new HazelException("A socket exception occured while reading data.", e));
                return;
            }

            //Exit if no bytes read, we've failed.
            if (bytesReceived == 0)
            {
                HandleDisconnect();
                return;
            }

            //Copy to new buffer
            byte[] buffer = new byte[bytesReceived];
            Buffer.BlockCopy((byte[])result.AsyncState, 1, buffer, 0, bytesReceived - 1);

            //Begin receiving again
            try
            {
                lock (socket)
                    StartListeningForData();
            }
            catch (SocketException e)
            {
                HandleDisconnect(new HazelException("A Socket exception occured while initiating a receive operation.", e));
            }

            Statistics.LogReceive(buffer.Length - 1, buffer.Length);

            InvokeDataReceived(new DataEventArgs(buffer));
        }

        /// <summary>
        ///     Called when the socket has been disconnected at the remote host.
        /// </summary>
        /// <param name="e">The exception if one was the cause.</param>
        void HandleDisconnect(HazelException e = null)
        {
            bool invoke = false;

            lock (socket)
            {
                //Only invoke the disconnected event if we're not already disconnecting
                if (State == ConnectionState.Connected)
                {
                    State = ConnectionState.Disconnecting;
                    invoke = true;
                }
            }

            //Invoke event outide lock if need be
            if (invoke)
            {
                InvokeDisconnected(new DisconnectedEventArgs(e));

                Dispose();
            }
        }

        /// <summary>
        ///     Safely closes this connection.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            //Dispose of the socket
            if (disposing)
            {
                lock (socket)
                {
                    State = ConnectionState.NotConnected;

                    socket.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}