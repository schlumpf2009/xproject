﻿// The MIT License (MIT)

// Copyright 2015 Siney/Pangweiwei siney@yeah.net
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

// Comment out this line to switch off remote debugger for slua
#define LuaDebugger

namespace SLua
{
    using System.Collections.Generic;
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.IO;
    using System.Reflection;

    public class DebugInterface : LuaObject
    {
        LuaState state;
        Socket server;
        Socket client;
        UdpClient replyClient;
        bool start = false;

        byte[] recvBuffer = new byte[1024];
        bool debugMode = false;
        string breakFileName = null;
        int breakFileLine = -1;
        int packageLen = 0;
        int nReadBytes = 0;
        Dictionary<string, string[]> luaSource = new Dictionary<string, string[]>();
        static Dictionary<string, string> sourceMd5 = new Dictionary<string, string>();
        static Dictionary<string, string> md5Source = new Dictionary<string, string>();

        int DebugPort = 10240;
        string DebugIP = "0.0.0.0";
        int logLevel = 2;   //0:none 1:err only 2:all

        static DebugInterface instance;

        const string usageTips = @"
add break point					b <filename>:<lineno>
remove break point              delete <break point index>
list exists break points		list
clear all break points          clear
set log level                   log <level>
-------------Debug Command-------------
continue                  		c
next                			n
step              				s
print traceback              	bt
watch local/up value  			watch
";

        public DebugInterface(LuaState L)
        {
            state = L;
            instance = this;

            IntPtr l = L.L;
            getTypeTable(l, "LuaDebugger");
            addMember(l, output, false);
            addMember(l, onBreak, false);
            addMember(l, md5, false);
            createTypeMetatable(l, typeof(DebugInterface));
        }

        public static void require(string f, byte[] bytes)
        {
#if LuaDebugger
            System.Security.Cryptography.MD5CryptoServiceProvider md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
            byte[] hashBytes = md5.ComputeHash(bytes);

            // Convert the encrypted bytes back to a string (base 16)
            string hashString = "";

            for (int i = 0; i < hashBytes.Length; i++)
            {
                hashString += System.Convert.ToString(hashBytes[i], 16).PadLeft(2, '0');
            }

            string m = hashString.PadLeft(32, '0');
            sourceMd5[m] = f.ToLower();
            md5Source[f.ToLower()] = m;
#endif
        }


        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int output(IntPtr l)
        {
            try
            {
                string str;
                LuaObject.checkType(l, 1, out str);
                instance.echo(str);
                //if(LuaState.logDelegate!=null)
                    //LuaState.logDelegate(str);
                pushValue(l, true);
                return 1;
            }
            catch (Exception e)
            {
                return error(l, e);
            }
        }

        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int onBreak(IntPtr l)
        {
            try
            {
                string f;
                checkType(l, 1, out f);
                int line;
                checkType(l, 2, out line);
                string md5;
                checkType(l, 3, out md5);
                instance.onBreak(f, line, md5);
                pushValue(l, true);
                return 1;
            }
            catch (Exception e)
            {
                return error(l, e);
            }
        }

        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int md5(IntPtr l)
        {
            try
            {
                string f;
                checkType(l, 1, out f);
                string md5 = instance.md5(f);
                pushValue(l, true);
                pushValue(l, md5);
                return 2;
            }
            catch (Exception e)
            {
                return error(l, e);
            }
        }


        string fetchLuaSource(string fileName, int line = -1, int range = 3)
        {
            if (!luaSource.ContainsKey(fileName))
            {
                byte[] bytes = LuaState.loadFile(fileName);
                if (bytes == null)
                {
                    return null;
                }

                string text = System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
                text = text.Replace("\r\n", "\n");
                string[] splitLines = text.Split(new char[] { '\n' });
                luaSource.Add(fileName, splitLines);
            }


            var lines = luaSource[fileName];


            int start, end;
            if (line >= 0)
            {
                start = line - range;
                end = line + range;
                if (start < 0) start = 0;
                if (end >= lines.Length) end = lines.Length;
            }
            else
            {
                start = 0;
                end = lines.Length;
            }

            string ret = "";
            for (int n = start; n < end; n++)
            {
                ret += string.Format("{0}{1}\t", n + 1, n == line ? ">" : "");
                ret += lines[n];
                ret += "\n";
            }
            return ret;

        }

        public void init()
        {
#if LuaDebugger
            try
            {
                IPEndPoint localEP = new IPEndPoint(IPAddress.Parse(DebugIP), DebugPort);
                server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
                server.Bind(localEP);
                server.Listen(10);
                server.BeginAccept(new AsyncCallback(onClientConnect), server);
                Logger.Log("Opened lua debugger interface at " + localEP.ToString());

                // redirect output to client socket
                //var luaFunc = state.getFunction("Slua.ldb.setOutput");
                //luaFunc.call((LuaCSFunction)output);

                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 10241);
                replyClient = new UdpClient(ep);
                replyClient.BeginReceive(new AsyncCallback(onReplyRecv), null);
            }
            catch (Exception e)
            {
                Logger.LogError(string.Format("LuaDebugger listened failed for reason:：{0}", e.Message));
            }
#endif
        }

        private void onReplyRecv(IAsyncResult ar)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 10241);
                Byte[] receiveBytes = replyClient.EndReceive(ar, ref ep);
                string msg = System.Text.UTF8Encoding.UTF8.GetString(receiveBytes);
                if (msg == "are you ok?")
                {
                    List<string> pack = new List<string>();
                    string platform = UnityEngine.Application.platform.ToString();
                    pack.Add(platform);
                    pack.Add(string.Format("{0} - {1}", System.Environment.MachineName, System.Environment.UserName));
                    pack.Add(DebugPort.ToString());
                    List<byte> byteLst = new List<byte>();
                    for (int i = 0; i < pack.Count; ++i)
                    {
                        string s = pack[i];
                        byteLst.AddRange(BitConverter.GetBytes(s.Length));
                        byteLst.AddRange(System.Text.UTF8Encoding.UTF8.GetBytes(s));
                    }
                    byte[] bytes = byteLst.ToArray();
                    replyClient.Send(bytes, bytes.Length, ep);
                }
                replyClient.BeginReceive(onReplyRecv, null);
            }
            catch (Exception e)
            {
                Logger.LogError(e.Message);
            }
        }

        public void update()
        {
#if LuaDebugger
            if (client == null || !client.Connected)
                return;

            process();
#endif
        }

        void error(string err)
        {
            send("ret bad {0}", err);
        }

        void ok(string str)
        {
            send("ret ok {0}", str);
        }

        void process()
        {
            while (true)
            {
                if (client == null || !client.Connected)
                    break;

                int len;
                try
                {
                    if (recvCmd(out len))
                    {
                        string str = System.Text.Encoding.UTF8.GetString(recvBuffer, 0, len);
                        str = str.Trim();

                        try
                        {
                            if (doCommand(str))
                                send("ret ok");
                            else
                                send("ret bad");
                        }
                        catch (Exception e)
                        {
                            error(e.Message);
                            Logger.LogError(e.Message);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception e)
                {
                    error(e.Message);
                    onClientDisconnect();
                    break;
                }
            }
        }

        bool recvCmd(out int len)
        {
            len = 0;
            SocketError error;
            if (packageLen == 0)
            {
                const int headSize = sizeof(int);
                int nRead = client.Receive(recvBuffer, nReadBytes, headSize - nReadBytes, SocketFlags.None, out error);
                nReadBytes += nRead;
                if (error == SocketError.WouldBlock)
                {
                    return false;
                }
                else if (nRead == 0 || error != SocketError.Success)
                {
                    throw new SocketException();
                }

                if (nReadBytes == headSize)
                {
                    packageLen = BitConverter.ToInt32(recvBuffer, 0);
                    if (packageLen > recvBuffer.Length)
                    {
                        Array.Resize<byte>(ref recvBuffer, packageLen);
                    }
                    nReadBytes = 0;
                }
            }
            else if (packageLen < 0)
            {
                Logger.LogError("Invalid packaged received.");
            }
            else
            {
                int nRead = client.Receive(recvBuffer, nReadBytes, packageLen - nReadBytes, SocketFlags.None, out error);
                nReadBytes += nRead;
                if (error == SocketError.WouldBlock)
                {
                    return false;
                }
                else if (nRead == 0 || error != SocketError.Success)
                {
                    throw new SocketException();
                }

                if (nReadBytes == packageLen)
                {
                    len = packageLen;
                    packageLen = 0;
                    nReadBytes = 0;
                    return true;
                }
            }
            return false;
        }


        public void send(string str)
        {
            try
            {
                if (client != null && client.Connected)
                {
                    client.Blocking = true;
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(str);
                    int bytelen = bytes.Length;
                    byte[] len = BitConverter.GetBytes(bytelen);
                    client.Send(len);
                    client.Send(bytes);
                    client.Blocking = false;
                }
            }
            catch (Exception e)
            {
                Logger.LogError(e.Message);
            }
        }

        public void send(string fmt, params object[] args)
        {
            string str = string.Format(fmt, args);
            send(str);
        }

        public void echo(string str)
        {
            send("print " + str);
        }

        public bool isStarted
        {
            get
            {
                return client != null && client.Connected && start;
            }
        }

        void onClientConnect(IAsyncResult target)
        {
            if (server == null)
                return;

            client = server.EndAccept(target);
            client.Blocking = false;

            server.BeginAccept(new AsyncCallback(onClientConnect), server);

            debugMode = false;

            LuaState.logDelegate -= addLog;
            LuaState.errorDelegate -= addError;

            LuaState.logDelegate += addLog;
            LuaState.errorDelegate += addError;

            Logger.Log("New debug session connected");
        }

        private void addError(string msg)
        {
            if (logLevel < 2)
                return;

            error(msg);
        }

        private void addLog(string msg)
        {
            if (logLevel < 1)
                return;

            echo(msg);
        }

        public void close()
        {
            if (client != null && client.Connected)
            {
                client.Close();
                client = null;
            }

            if (server != null)
            {
                try
                {
                    server.Shutdown(SocketShutdown.Both);
                }
                catch (Exception)
                { }
                server.Close();
                server = null;
            }

            Logger.Log("Closed lua debugger interface.");

        }

        void onClientDisconnect()
        {
            state.doString("Slua.ldb.clearBreakPoint()");

            debugMode = false;
            client.Close();
            client = null;

            LuaState.logDelegate -= addLog;
            LuaState.errorDelegate -= addError;

            Logger.Log("Debug session disconnected");
        }

        public string md5(string f)
        {
            string md5;
            if (md5Source.TryGetValue(f, out md5))
                return md5;
            return null;
        }

        public void onBreak(string f, int line, string md5)
        {
            echo(f + ".lua");
            echo(fetchLuaSource(f, line - 1, 5));

            breakFileName = f;
            breakFileLine = line;

            debugMode = true;
            while (debugMode && (client != null))
            {
                process();
                System.Threading.Thread.Sleep(100);
            }
            send("resume");
        }

        bool cmdquit(string tail)
        {
            onClientDisconnect();
            return true;
        }

        bool cmdstart(string tail)
        {
            start = true;
            return true;
        }

        bool cmdfs(string tail)
        {
            if (tail == "")
            {
                if (debugMode)
                {
                    echo(fetchLuaSource(breakFileName, breakFileLine, 5));
                    return false;
                }
                error("arg");
                return false;
            }

            string[] fileNameAndLine = tail.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            string fileName = fileNameAndLine[0];
            int line = -1, range = 3;
            if (fileNameAndLine.Length > 1)
                line = int.Parse(fileNameAndLine[1]);
            if (fileNameAndLine.Length > 2)
                range = int.Parse(fileNameAndLine[2]);


            echo(fetchLuaSource(fileName, line, range));
            return false;
        }


        bool cmdb(string tail)
        {
            if (tail == "")
            {
                error("arg");
                return false;
            }

            string[] fileNameAndLine = tail.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            string fileName = fileNameAndLine[0];
            if (Path.HasExtension(fileName))
            {
                string ext = Path.GetExtension(fileName);
                fileName = fileName.Substring(0, fileName.Length - ext.Length);
            }
            int line = int.Parse(fileNameAndLine[1]);

            var luaFunc = state.getFunction("Slua.ldb.addBreakPoint");
            int bp = Convert.ToInt32(luaFunc.call(fileName, line));
            echo(string.Format("set break point #{0} at {1}.lua:{2}", bp, fileName, line));
            echo(fetchLuaSource(fileName, line - 1, 2));
            return true;
        }

        //bool cmdb5(string tail)
        //{
        //    if (tail == "")
        //    {
        //        error("arg");
        //        return false;
        //    }

        //    string[] fileNameAndLine = tail.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        //    string md5 = fileNameAndLine[0];
        //    int line = int.Parse(fileNameAndLine[1]);
        //    var luaFunc = state.getFunction("Slua.ldb.addBreakPointMD5");
        //    int bp = Convert.ToInt32(luaFunc.call(md5, line));
        //    echo(string.Format("set break point #{0} at {1}:{2}", bp, md5, line));
        //    return true;
        //}

        bool deletebp(string tail)
        {
            if (tail == "")
            {
                error("arg");
                return false;
            }

            int breakPointIndex = int.Parse(tail);
            var luaFunc = state.getFunction("Slua.ldb.delBreakPoint");
            luaFunc.call(breakPointIndex);
            return true;
        }

        bool cmddelete(string tail)
        {
            return deletebp(tail);
        }

        bool cmddel(string tail)
        {
            deletebp(tail);
            return true;
        }

        bool cmdlist(string tail)
        {
            state.doString("Slua.ldb.showBreakPointList()");
            return true;
        }

        bool cmdhelp(string tail)
        {
            echo(usageTips);
            return true;
        }

        bool cmdclear(string tail)
        {
            state.doString("Slua.ldb.clearBreakPoint()");
            return true;
        }

        bool cmdc(string tail)
        {
            if (!debugMode)
                return false;
            debugMode = false;
            state.doString("Slua.ldb.continue()");
            return true;
        }

        bool cmds(string tail)
        {
            if (!debugMode)
                return false;
            debugMode = false;
            state.doString("Slua.ldb.stepIn()");
            return true;
        }

        bool cmdn(string tail)
        {
            if (!debugMode)
                return false;
            debugMode = false;
            state.doString("Slua.ldb.stepOver()");
            return true;
        }

        bool cmdbt(string bt)
        {
            if (!debugMode)
                return false;

            state.doString("Slua.ldb.bt()");
            return true;
        }


        bool cmdwatch(string bt)
        {
            if (!debugMode)
                return false;

            state.doString("Slua.ldb.watch()");
            return true;
        }

        bool cmdp(string r)
        {
            var luaFunc = state.getFunction("Slua.ldb.printExpr");
            luaFunc.call(r);
            return true;
        }

        bool cmdlog(string level)
        {
            if (level == "0")
            {
                logLevel = 0;
            }
            else if (level == "1")
            {
                logLevel = 1;
            }
            else if (level == "2")
            {
                logLevel = 2;
            }
            else if (level == "")
            {
                echo(string.Format("current log level = {0}", logLevel));
            }
            else
            {
                error("logcat 0/1/2, 0:none 1:error only 2:all");
            }
            return true;
        }

        bool doCommand(string str)
        {
            int index = str.IndexOf(" ");
            string cmd = str;
            string tail = "";
            if (index > 0)
            {
                cmd = str.Substring(0, index).Trim().ToLower();
                tail = str.Substring(index + 1);
            }


            cmd = "cmd" + cmd;
            MethodInfo mi = this.GetType().GetMethod(cmd, BindingFlags.Instance | BindingFlags.NonPublic);
            if (mi != null)
            {
                return (bool)mi.Invoke(this, new object[] { tail });
            }
            else
            {
                if (!string.IsNullOrEmpty(str))
                {
                    var luaFunc = state.getFunction("Slua.ldb.printExpr");
                    object[] rets = (object[])luaFunc.call(str);
                    if (((bool)rets[0]) == false)
                    {
                        error(rets[1] as string);
                        return false;
                    }
                    return true;
                }
            }

            return true;
        }
    }
}