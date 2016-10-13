// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Threading;

using KERB_LOGON_SUBMIT_TYPE = Interop.SspiCli.KERB_LOGON_SUBMIT_TYPE;
using KERB_S4U_LOGON = Interop.SspiCli.KERB_S4U_LOGON;
using KerbS4uLogonFlags = Interop.SspiCli.KerbS4uLogonFlags;
using LUID = Interop.LUID;
using LSA_STRING = Interop.SspiCli.LSA_STRING;
using QUOTA_LIMITS = Interop.SspiCli.QUOTA_LIMITS;
using SECURITY_LOGON_TYPE = Interop.SspiCli.SECURITY_LOGON_TYPE;
using TOKEN_SOURCE = Interop.SspiCli.TOKEN_SOURCE;

namespace System.Security.Principal
{
    public class WindowsIdentity : ClaimsIdentity, IDisposable
    {
        private string _name = null;
        private SecurityIdentifier _owner = null;
        private SecurityIdentifier _user = null;
        private IdentityReferenceCollection _groups = null;

        private SafeAccessTokenHandle _safeTokenHandle = SafeAccessTokenHandle.InvalidHandle;
        private string _authType = null;
        private int _isAuthenticated = -1;
        private volatile TokenImpersonationLevel _impersonationLevel;
        private volatile bool _impersonationLevelInitialized;

        public new const string DefaultIssuer = @"AD AUTHORITY";
        private string _issuerName = DefaultIssuer;
        private object _claimsIntiailizedLock = new object();
        private volatile bool _claimsInitialized;
        private List<Claim> _deviceClaims;
        private List<Claim> _userClaims;

        //
        // Constructors.
        //

        private WindowsIdentity()
            : base(null, null, null, ClaimTypes.Name, ClaimTypes.GroupSid)
        { }

        /// <summary>
        /// Initializes a new instance of the WindowsIdentity class for the user represented by the specified User Principal Name (UPN).
        /// </summary>
        /// <remarks>
        /// Unlike the desktop version, we connect to Lsa only as an untrusted caller. We do not attempt to explot Tcb privilege or adjust the current
        /// thread privilege to include Tcb.
        /// </remarks>
        public WindowsIdentity(string sUserPrincipalName)
            : base(null, null, null, ClaimTypes.Name, ClaimTypes.GroupSid)
        {
            // Desktop compat: See comments below for why we don't validate sUserPrincipalName.

            using (SafeLsaHandle lsaHandle = ConnectToLsa())
            {
                int packageId = LookupAuthenticationPackage(lsaHandle, Interop.SspiCli.AuthenticationPackageNames.MICROSOFT_KERBEROS_NAME_A);

                // 8 byte or less name that indicates the source of the access token. This choice of name is visible to callers through the native GetTokenInformation() api
                // so we'll use the same name the CLR used even though we're not actually the "CLR."
                byte[] sourceName = { (byte)'C', (byte)'L', (byte)'R', (byte)0 };

                TOKEN_SOURCE sourceContext;
                if (!Interop.SecurityBase.AllocateLocallyUniqueId(out sourceContext.SourceIdentifier))
                    throw new SecurityException(new Win32Exception().Message);
                sourceContext.SourceName = new byte[TOKEN_SOURCE.TOKEN_SOURCE_LENGTH];
                Buffer.BlockCopy(sourceName, 0, sourceContext.SourceName, 0, sourceName.Length);

                // Desktop compat: Desktop never null-checks sUserPrincipalName. Actual behavior is that the null makes it down to Encoding.Unicode.GetBytes() which then throws
                // the ArgumentNullException (provided that the prior LSA calls didn't fail first.) To make this compat decision explicit, we'll null check ourselves 
                // and simulate the exception from Encoding.Unicode.GetBytes().
                if (sUserPrincipalName == null)
                    throw new ArgumentNullException("s");

                byte[] upnBytes = Encoding.Unicode.GetBytes(sUserPrincipalName);
                if (upnBytes.Length > ushort.MaxValue)
                {
                    // Desktop compat: LSA only allocates 16 bits to hold the UPN size. We should throw an exception here but unfortunately, the desktop did an unchecked cast to ushort,
                    // effectively truncating upnBytes to the first (N % 64K) bytes. We'll simulate the same behavior here (albeit in a way that makes it look less accidental.)
                    Array.Resize(ref upnBytes, upnBytes.Length & ushort.MaxValue);
                }
                unsafe
                {
                    //
                    // Build the KERB_S4U_LOGON structure.  Note that the LSA expects this entire
                    // structure to be contained within the same block of memory, so we need to allocate
                    // enough room for both the structure itself and the UPN string in a single buffer
                    // and do the marshalling into this buffer by hand.
                    //
                    int authenticationInfoLength = checked(sizeof(KERB_S4U_LOGON) + upnBytes.Length);
                    using (SafeLocalAllocHandle authenticationInfo = Interop.mincore_obsolete.LocalAlloc(0, new UIntPtr(checked((uint)authenticationInfoLength))))
                    {
                        if (authenticationInfo.IsInvalid)
                            throw new OutOfMemoryException();

                        KERB_S4U_LOGON* pKerbS4uLogin = (KERB_S4U_LOGON*)(authenticationInfo.DangerousGetHandle());
                        pKerbS4uLogin->MessageType = KERB_LOGON_SUBMIT_TYPE.KerbS4ULogon;
                        pKerbS4uLogin->Flags = KerbS4uLogonFlags.None;

                        pKerbS4uLogin->ClientUpn.Length = pKerbS4uLogin->ClientUpn.MaximumLength = checked((ushort)upnBytes.Length);

                        IntPtr pUpnOffset = (IntPtr)(pKerbS4uLogin + 1);
                        pKerbS4uLogin->ClientUpn.Buffer = pUpnOffset;
                        Marshal.Copy(upnBytes, 0, pKerbS4uLogin->ClientUpn.Buffer, upnBytes.Length);

                        pKerbS4uLogin->ClientRealm.Length = pKerbS4uLogin->ClientRealm.MaximumLength = 0;
                        pKerbS4uLogin->ClientRealm.Buffer = IntPtr.Zero;

                        ushort sourceNameLength = checked((ushort)(sourceName.Length));
                        using (SafeLocalAllocHandle sourceNameBuffer = Interop.mincore_obsolete.LocalAlloc(0, new UIntPtr(sourceNameLength)))
                        {
                            if (sourceNameBuffer.IsInvalid)
                                throw new OutOfMemoryException();

                            Marshal.Copy(sourceName, 0, sourceNameBuffer.DangerousGetHandle(), sourceName.Length);
                            LSA_STRING lsaOriginName = new LSA_STRING(sourceNameBuffer.DangerousGetHandle(), sourceNameLength);

                            SafeLsaReturnBufferHandle profileBuffer;
                            int profileBufferLength;
                            LUID logonId;
                            SafeAccessTokenHandle accessTokenHandle;
                            QUOTA_LIMITS quota;
                            int subStatus;
                            int ntStatus = Interop.SspiCli.LsaLogonUser(
                                lsaHandle,
                                ref lsaOriginName,
                                SECURITY_LOGON_TYPE.Network,
                                packageId,
                                authenticationInfo.DangerousGetHandle(),
                                authenticationInfoLength,
                                IntPtr.Zero,
                                ref sourceContext,
                                out profileBuffer,
                                out profileBufferLength,
                                out logonId,
                                out accessTokenHandle,
                                out quota,
                                out subStatus);

                            if (ntStatus == unchecked((int)Interop.StatusOptions.STATUS_ACCOUNT_RESTRICTION) && subStatus < 0)
                                ntStatus = subStatus;
                            if (ntStatus < 0) // non-negative numbers indicate success
                                throw GetExceptionFromNtStatus(ntStatus);
                            if (subStatus < 0) // non-negative numbers indicate success
                                throw GetExceptionFromNtStatus(subStatus);

                            if (profileBuffer != null)
                                profileBuffer.Dispose();

                            _safeTokenHandle = accessTokenHandle;
                        }
                    }
                }
            }
        }

        private static SafeLsaHandle ConnectToLsa()
        {
            SafeLsaHandle lsaHandle;
            int ntStatus = Interop.SspiCli.LsaConnectUntrusted(out lsaHandle);
            if (ntStatus < 0) // non-negative numbers indicate success
                throw GetExceptionFromNtStatus(ntStatus);
            return lsaHandle;
        }

        private static int LookupAuthenticationPackage(SafeLsaHandle lsaHandle, string packageName)
        {
            unsafe
            {
                int packageId;
                byte[] asciiPackageName = Encoding.ASCII.GetBytes(packageName);
                fixed (byte* pAsciiPackageName = asciiPackageName)
                {
                    LSA_STRING lsaPackageName = new LSA_STRING((IntPtr)pAsciiPackageName, checked((ushort)(asciiPackageName.Length)));
                    int ntStatus = Interop.SspiCli.LsaLookupAuthenticationPackage(lsaHandle, ref lsaPackageName, out packageId);
                    if (ntStatus < 0) // non-negative numbers indicate success
                        throw GetExceptionFromNtStatus(ntStatus);
                }
                return packageId;
            }
        }

        public WindowsIdentity(IntPtr userToken) : this(userToken, null, -1) { }


        public WindowsIdentity(IntPtr userToken, string type) : this(userToken, type, -1) { }


        private WindowsIdentity(IntPtr userToken, string authType, int isAuthenticated)
            : base(null, null, null, ClaimTypes.Name, ClaimTypes.GroupSid)
        {
            CreateFromToken(userToken);
            _authType = authType;
            _isAuthenticated = isAuthenticated;
        }


        private void CreateFromToken(IntPtr userToken)
        {
            if (userToken == IntPtr.Zero)
                throw new ArgumentException(SR.Argument_TokenZero);
            Contract.EndContractBlock();

            // Find out if the specified token is a valid.
            uint dwLength = (uint)Marshal.SizeOf<uint>();
            bool result = Interop.mincore.GetTokenInformation(userToken, (uint)TokenInformationClass.TokenType,
                                                          SafeLocalAllocHandle.InvalidHandle, 0, out dwLength);
            if (Marshal.GetLastWin32Error() == Interop.mincore.Errors.ERROR_INVALID_HANDLE)
                throw new ArgumentException(SR.Argument_InvalidImpersonationToken);

            if (!Interop.mincore.DuplicateHandle(Interop.mincore.GetCurrentProcess(),
                                             userToken,
                                             Interop.mincore.GetCurrentProcess(),
                                             ref _safeTokenHandle,
                                             0,
                                             true,
                                             Interop.DuplicateHandleOptions.DUPLICATE_SAME_ACCESS))
                throw new SecurityException(new Win32Exception().Message);
        }

        //
        // Factory methods.
        //
        
        public static WindowsIdentity GetCurrent()
        {
            return GetCurrentInternal(TokenAccessLevels.MaximumAllowed, false);
        }


        public static WindowsIdentity GetCurrent(bool ifImpersonating)
        {
            return GetCurrentInternal(TokenAccessLevels.MaximumAllowed, ifImpersonating);
        }


        public static WindowsIdentity GetCurrent(TokenAccessLevels desiredAccess)
        {
            return GetCurrentInternal(desiredAccess, false);
        }

        // GetAnonymous() is used heavily in ASP.NET requests as a dummy identity to indicate
        // the request is anonymous. It does not represent a real process or thread token so
        // it cannot impersonate or do anything useful. Note this identity does not represent the
        // usual concept of an anonymous token, and the name is simply misleading but we cannot change it now.
        
        public static WindowsIdentity GetAnonymous()
        {
            return new WindowsIdentity();
        }

        //
        // Properties.
        //
        // this is defined 'override sealed' for back compat. Il generated is 'virtual final' and this needs to be the same.
        public override sealed string AuthenticationType
        {
            get
            {
                // If this is an anonymous identity, return an empty string
                if (_safeTokenHandle.IsInvalid)
                    return String.Empty;

                if (_authType == null)
                {
                    Interop.LUID authId = GetLogonAuthId(_safeTokenHandle);
                    if (authId.LowPart == Interop.LuidOptions.ANONYMOUS_LOGON_LUID)
                        return String.Empty; // no authentication, just return an empty string

                    SafeLsaReturnBufferHandle pLogonSessionData = SafeLsaReturnBufferHandle.InvalidHandle;
                    try
                    {
                        int status = Interop.mincore.LsaGetLogonSessionData(ref authId, ref pLogonSessionData);
                        if (status < 0) // non-negative numbers indicate success
                            throw GetExceptionFromNtStatus(status);

                        pLogonSessionData.Initialize((uint)Marshal.SizeOf<Interop.SECURITY_LOGON_SESSION_DATA>());

                        Interop.SECURITY_LOGON_SESSION_DATA logonSessionData = pLogonSessionData.Read<Interop.SECURITY_LOGON_SESSION_DATA>(0);
                        return Marshal.PtrToStringUni(logonSessionData.AuthenticationPackage.Buffer);
                    }
                    finally
                    {
                        if (!pLogonSessionData.IsInvalid)
                            pLogonSessionData.Dispose();
                    }
                }

                return _authType;
            }
        }

        public TokenImpersonationLevel ImpersonationLevel
        {
            get
            {
                // In case of a race condition here here, both threads will set m_impersonationLevel to the same value,
                // which is ok.
                if (!_impersonationLevelInitialized)
                {
                    TokenImpersonationLevel impersonationLevel = TokenImpersonationLevel.None;
                    // If this is an anonymous identity
                    if (_safeTokenHandle.IsInvalid)
                    {
                        impersonationLevel = TokenImpersonationLevel.Anonymous;
                    }
                    else
                    {
                        TokenType tokenType = (TokenType)GetTokenInformation<int>(TokenInformationClass.TokenType);
                        if (tokenType == TokenType.TokenPrimary)
                        {
                            impersonationLevel = TokenImpersonationLevel.None; // primary token;
                        }
                        else
                        {
                            /// This is an impersonation token, get the impersonation level
                            int level = GetTokenInformation<int>(TokenInformationClass.TokenImpersonationLevel);
                            impersonationLevel = (TokenImpersonationLevel)level + 1;
                        }
                    }

                    _impersonationLevel = impersonationLevel;
                    _impersonationLevelInitialized = true;
                }

                return _impersonationLevel;
            }
        }

        public override bool IsAuthenticated
        {
            get
            {
                if (_isAuthenticated == -1)
                {
                        // This approach will not work correctly for domain guests (will return false
                        // instead of true). This is a corner-case that is not very interesting.
                        _isAuthenticated = CheckNtTokenForSid(new SecurityIdentifier(IdentifierAuthority.NTAuthority,
                                                                        new int[] { Interop.SecurityIdentifier.SECURITY_AUTHENTICATED_USER_RID })) ? 1 : 0;
                }
                return _isAuthenticated == 1;                
            }
        }
        private bool CheckNtTokenForSid(SecurityIdentifier sid)
        {
            Contract.EndContractBlock();

            // special case the anonymous identity.
            if (_safeTokenHandle.IsInvalid)
                return false;

            // CheckTokenMembership expects an impersonation token
            SafeAccessTokenHandle token = SafeAccessTokenHandle.InvalidHandle;
            TokenImpersonationLevel til = ImpersonationLevel;
            bool isMember = false;

            try
            {
                if (til == TokenImpersonationLevel.None)
                {
                    if (!Interop.mincore.DuplicateTokenEx(_safeTokenHandle,
                                                      (uint)TokenAccessLevels.Query,
                                                      IntPtr.Zero,
                                                      (uint)TokenImpersonationLevel.Identification,
                                                      (uint)TokenType.TokenImpersonation,
                                                      ref token))
                        throw new SecurityException(new Win32Exception().Message);
                }


                // CheckTokenMembership will check if the SID is both present and enabled in the access token.
                if (!Interop.mincore.CheckTokenMembership((til != TokenImpersonationLevel.None ? _safeTokenHandle : token),
                                                      sid.BinaryForm,
                                                      ref isMember))
                    throw new SecurityException(new Win32Exception().Message);
            }
            finally
            {
                if (token != SafeAccessTokenHandle.InvalidHandle)
                {
                    token.Dispose();
                }
            }

            return isMember;
        }

        //
        // IsGuest, IsSystem and IsAnonymous are maintained for compatibility reasons. It is always
        // possible to extract this same information from the User SID property and the new
        // (and more general) methods defined in the SID class (IsWellKnown, etc...).
        //

        public virtual bool IsGuest
        {
            get
            {
                // special case the anonymous identity.
                if (_safeTokenHandle.IsInvalid)
                    return false;

                return CheckNtTokenForSid(new SecurityIdentifier(IdentifierAuthority.NTAuthority,
                                                new int[] { Interop.SecurityIdentifier.SECURITY_BUILTIN_DOMAIN_RID, (int)WindowsBuiltInRole.Guest }));
            }
        }

        public virtual bool IsSystem
        {
            get
            {
                // special case the anonymous identity.
                if (_safeTokenHandle.IsInvalid)
                    return false;
                SecurityIdentifier sid = new SecurityIdentifier(IdentifierAuthority.NTAuthority,
                                                                new int[] { Interop.SecurityIdentifier.SECURITY_LOCAL_SYSTEM_RID });
                return (this.User == sid);
            }
        }

        public virtual bool IsAnonymous
        {
            get
            {
                // special case the anonymous identity.
                if (_safeTokenHandle.IsInvalid)
                    return true;
                SecurityIdentifier sid = new SecurityIdentifier(IdentifierAuthority.NTAuthority,
                                                                new int[] { Interop.SecurityIdentifier.SECURITY_ANONYMOUS_LOGON_RID });
                return (this.User == sid);
            }
        }

        public override string Name
        {
            get
            {
                return GetName();
            }
        }

        internal String GetName()
        {
            // special case the anonymous identity.
            if (_safeTokenHandle.IsInvalid)
                return String.Empty;

            if (_name == null)
            {
                // revert thread impersonation for the duration of the call to get the name.
                RunImpersonated(SafeAccessTokenHandle.InvalidHandle, delegate
                {
                    NTAccount ntAccount = this.User.Translate(typeof(NTAccount)) as NTAccount;
                    _name = ntAccount.ToString();
                });
            }

            return _name;
        }

        public SecurityIdentifier Owner
        {
            get
            {
                // special case the anonymous identity.
                if (_safeTokenHandle.IsInvalid)
                    return null;

                if (_owner == null)
                {
                    using (SafeLocalAllocHandle tokenOwner = GetTokenInformation(_safeTokenHandle, TokenInformationClass.TokenOwner))
                    {
                        _owner = new SecurityIdentifier(tokenOwner.Read<IntPtr>(0), true);
                    }
                }

                return _owner;
            }
        }

        public SecurityIdentifier User
        {
            get
            {
                // special case the anonymous identity.
                if (_safeTokenHandle.IsInvalid)
                    return null;

                if (_user == null)
                {
                    using (SafeLocalAllocHandle tokenUser = GetTokenInformation(_safeTokenHandle, TokenInformationClass.TokenUser))
                    {
                        _user = new SecurityIdentifier(tokenUser.Read<IntPtr>(0), true);
                    }
                }

                return _user;
            }
        }

        public IdentityReferenceCollection Groups
        {
            get
            {
                // special case the anonymous identity.
                if (_safeTokenHandle.IsInvalid)
                    return null;

                if (_groups == null)
                {
                    IdentityReferenceCollection groups = new IdentityReferenceCollection();
                    using (SafeLocalAllocHandle pGroups = GetTokenInformation(_safeTokenHandle, TokenInformationClass.TokenGroups))
                    {
                        uint groupCount = pGroups.Read<uint>(0);

                        Interop.TOKEN_GROUPS tokenGroups = pGroups.Read<Interop.TOKEN_GROUPS>(0);
                        Interop.SID_AND_ATTRIBUTES[] groupDetails = new Interop.SID_AND_ATTRIBUTES[tokenGroups.GroupCount];
                        pGroups.ReadArray((uint)Marshal.OffsetOf<Interop.TOKEN_GROUPS>("Groups").ToInt32(),

                                          groupDetails,
                                          0,
                                          groupDetails.Length);

                        foreach (Interop.SID_AND_ATTRIBUTES group in groupDetails)
                        {
                            // Ignore disabled, logon ID, and deny-only groups.
                            uint mask = Interop.SecurityGroups.SE_GROUP_ENABLED | Interop.SecurityGroups.SE_GROUP_LOGON_ID | Interop.SecurityGroups.SE_GROUP_USE_FOR_DENY_ONLY;
                            if ((group.Attributes & mask) == Interop.SecurityGroups.SE_GROUP_ENABLED)
                            {
                                groups.Add(new SecurityIdentifier(group.Sid, true));
                            }
                        }
                    }
                    Interlocked.CompareExchange(ref _groups, groups, null);
                }

                return _groups;
            }
        }

        public SafeAccessTokenHandle AccessToken
        {
            get
            {
                return _safeTokenHandle;
            }
        }

        public virtual IntPtr Token
        {
            get
            {
                return _safeTokenHandle.DangerousGetHandle();
            }
        }

        //
        // Public methods.
        //

        public static void RunImpersonated(SafeAccessTokenHandle safeAccessTokenHandle, Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            RunImpersonatedInternal(safeAccessTokenHandle, action);
        }


        public static T RunImpersonated<T>(SafeAccessTokenHandle safeAccessTokenHandle, Func<T> func)
        {
            if (func == null)
                throw new ArgumentNullException(nameof(func));

            T result = default(T);
            RunImpersonatedInternal(safeAccessTokenHandle, () => result = func());
            return result;
        }


        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_safeTokenHandle != null && !_safeTokenHandle.IsClosed)
                    _safeTokenHandle.Dispose();
            }
            _name = null;
            _owner = null;
            _user = null;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        //
        // internal.
        //
        
        private static AsyncLocal<SafeAccessTokenHandle> s_currentImpersonatedToken = new AsyncLocal<SafeAccessTokenHandle>(CurrentImpersonatedTokenChanged);
        
        private static void RunImpersonatedInternal(SafeAccessTokenHandle token, Action action)
        {
            bool isImpersonating;
            int hr;
            SafeAccessTokenHandle previousToken = GetCurrentToken(TokenAccessLevels.MaximumAllowed, false, out isImpersonating, out hr);
            if (previousToken == null || previousToken.IsInvalid)
                throw new SecurityException(new Win32Exception(hr).Message);

            s_currentImpersonatedToken.Value = isImpersonating ? previousToken : null;

            ExecutionContext currentContext = ExecutionContext.Capture();

            // Run everything else inside of ExecutionContext.Run, so that any EC changes will be undone
            // on the way out.
            ExecutionContext.Run(
                currentContext,
                delegate
                {
                    if (!Interop.mincore.RevertToSelf())
                        Environment.FailFast(new Win32Exception().Message);

                    s_currentImpersonatedToken.Value = null;

                    if (!token.IsInvalid && !Interop.mincore.ImpersonateLoggedOnUser(token))
                        throw new SecurityException(SR.Argument_ImpersonateUser);

                    s_currentImpersonatedToken.Value = token;

                    action();
                },
                null);
        }
        
        private static void CurrentImpersonatedTokenChanged(AsyncLocalValueChangedArgs<SafeAccessTokenHandle> args)
        {
            if (!args.ThreadContextChanged)
                return; // we handle explicit Value property changes elsewhere.

            if (!Interop.mincore.RevertToSelf())
                Environment.FailFast(new Win32Exception().Message);

            if (args.CurrentValue != null && !args.CurrentValue.IsInvalid)
            {
                if (!Interop.mincore.ImpersonateLoggedOnUser(args.CurrentValue))
                    Environment.FailFast(new Win32Exception().Message);
            }
        }
        
        internal static WindowsIdentity GetCurrentInternal(TokenAccessLevels desiredAccess, bool threadOnly)
        {
            int hr = 0;
            bool isImpersonating;
            SafeAccessTokenHandle safeTokenHandle = GetCurrentToken(desiredAccess, threadOnly, out isImpersonating, out hr);
            if (safeTokenHandle == null || safeTokenHandle.IsInvalid)
            {
                // either we wanted only ThreadToken - return null
                if (threadOnly && !isImpersonating)
                    return null;
                // or there was an error
                throw new SecurityException(new Win32Exception(hr).Message);
            }
            WindowsIdentity wi = new WindowsIdentity();
            wi._safeTokenHandle.Dispose();
            wi._safeTokenHandle = safeTokenHandle;
            return wi;
        }

        //
        // private.
        //
        private static int GetHRForWin32Error(int dwLastError)
        {
            if ((dwLastError & 0x80000000) == 0x80000000)
                return dwLastError;
            else
                return (dwLastError & 0x0000FFFF) | unchecked((int)0x80070000);
        }
        
        private static Exception GetExceptionFromNtStatus(int status)
        {
            if ((uint)status == Interop.StatusOptions.STATUS_ACCESS_DENIED)
                return new UnauthorizedAccessException();

            if ((uint)status == Interop.StatusOptions.STATUS_INSUFFICIENT_RESOURCES || (uint)status == Interop.StatusOptions.STATUS_NO_MEMORY)
                return new OutOfMemoryException();

            int win32ErrorCode = Interop.mincore.RtlNtStatusToDosError(status);
            return new SecurityException(new Win32Exception(win32ErrorCode).Message);
        }
        
        private static SafeAccessTokenHandle GetCurrentToken(TokenAccessLevels desiredAccess, bool threadOnly, out bool isImpersonating, out int hr)
        {
            isImpersonating = true;
            SafeAccessTokenHandle safeTokenHandle;
            hr = 0;
            bool success = Interop.mincore.OpenThreadToken(desiredAccess, WinSecurityContext.Both, out safeTokenHandle);
            if (!success)
                hr = Marshal.GetHRForLastWin32Error();
            if (!success && hr == GetHRForWin32Error(Interop.mincore.Errors.ERROR_NO_TOKEN))
            {
                // No impersonation
                isImpersonating = false;
                if (!threadOnly)
                    safeTokenHandle = GetCurrentProcessToken(desiredAccess, out hr);
            }
            return safeTokenHandle;
        }

        private static SafeAccessTokenHandle GetCurrentProcessToken(TokenAccessLevels desiredAccess, out int hr)
        {
            hr = 0;
            SafeAccessTokenHandle safeTokenHandle;
            if (!Interop.mincore.OpenProcessToken(Interop.mincore.GetCurrentProcess(), desiredAccess, out safeTokenHandle))
                hr = GetHRForWin32Error(Marshal.GetLastWin32Error());
            return safeTokenHandle;
        }

        /// <summary>
        ///   Get a property from the current token
        /// </summary>

        private T GetTokenInformation<T>(TokenInformationClass tokenInformationClass) where T : struct
        {
            Debug.Assert(!_safeTokenHandle.IsInvalid && !_safeTokenHandle.IsClosed, "!m_safeTokenHandle.IsInvalid && !m_safeTokenHandle.IsClosed");

            using (SafeLocalAllocHandle information = GetTokenInformation(_safeTokenHandle, tokenInformationClass))
            {
                Debug.Assert(information.ByteLength >= (ulong)Marshal.SizeOf<T>(),
                                "information.ByteLength >= (ulong)Marshal.SizeOf(typeof(T))");

                return information.Read<T>(0);
            }
        }

        //
        // QueryImpersonation used to test if the current thread is impersonated.
        // This method doesn't return the thread token (WindowsIdentity).
        // Note GetCurrentInternal can be used to perform the same test, but 
        // QueryImpersonation is optimized for performance
        // 

        internal static ImpersonationQueryResult QueryImpersonation()
        {
            SafeAccessTokenHandle safeTokenHandle = null;
            bool success = Interop.mincore.OpenThreadToken(TokenAccessLevels.Query, WinSecurityContext.Thread, out safeTokenHandle);

            if (safeTokenHandle != null)
            {
                Debug.Assert(success, "[WindowsIdentity..QueryImpersonation] - success");
                safeTokenHandle.Dispose();
                return ImpersonationQueryResult.Impersonated;
            }

            int lastError = Marshal.GetLastWin32Error();

            if (lastError == Interop.mincore.Errors.ERROR_ACCESS_DENIED)
            {
                // thread is impersonated because the thread was there (and we failed to open it).
                return ImpersonationQueryResult.Impersonated;
            }

            if (lastError == Interop.mincore.Errors.ERROR_NO_TOKEN)
            {
                // definitely not impersonating
                return ImpersonationQueryResult.NotImpersonated;
            }

            // Unexpected failure.
            return ImpersonationQueryResult.Failed;
        }


        private static Interop.LUID GetLogonAuthId(SafeAccessTokenHandle safeTokenHandle)
        {
            using (SafeLocalAllocHandle pStatistics = GetTokenInformation(safeTokenHandle, TokenInformationClass.TokenStatistics))
            {
                Interop.TOKEN_STATISTICS statistics = pStatistics.Read<Interop.TOKEN_STATISTICS>(0);
                return statistics.AuthenticationId;
            }
        }


        private static SafeLocalAllocHandle GetTokenInformation(SafeAccessTokenHandle tokenHandle, TokenInformationClass tokenInformationClass)
        {
            SafeLocalAllocHandle safeLocalAllocHandle = SafeLocalAllocHandle.InvalidHandle;
            uint dwLength = (uint)Marshal.SizeOf<uint>();
            bool result = Interop.mincore.GetTokenInformation(tokenHandle,
                                                          (uint)tokenInformationClass,
                                                          safeLocalAllocHandle,
                                                          0,
                                                          out dwLength);
            int dwErrorCode = Marshal.GetLastWin32Error();
            switch (dwErrorCode)
            {
                case Interop.mincore.Errors.ERROR_BAD_LENGTH:
                // special case for TokenSessionId. Falling through
                case Interop.mincore.Errors.ERROR_INSUFFICIENT_BUFFER:
                    // ptrLength is an [In] param to LocalAlloc 
                    UIntPtr ptrLength = new UIntPtr(dwLength);
                    safeLocalAllocHandle.Dispose();
                    safeLocalAllocHandle = Interop.mincore_obsolete.LocalAlloc(0, ptrLength);
                    if (safeLocalAllocHandle == null || safeLocalAllocHandle.IsInvalid)
                        throw new OutOfMemoryException();
                    safeLocalAllocHandle.Initialize(dwLength);

                    result = Interop.mincore.GetTokenInformation(tokenHandle,
                                                             (uint)tokenInformationClass,
                                                             safeLocalAllocHandle,
                                                             dwLength,
                                                             out dwLength);
                    if (!result)
                        throw new SecurityException(new Win32Exception().Message);
                    break;
                case Interop.mincore.Errors.ERROR_INVALID_HANDLE:
                    throw new ArgumentException(SR.Argument_InvalidImpersonationToken);
                default:
                    throw new SecurityException(new Win32Exception(dwErrorCode).Message);
            }
            return safeLocalAllocHandle;
        }

        protected WindowsIdentity(WindowsIdentity identity)
            : base(identity, null, GetAuthType(identity), null, null)
        {
            bool mustDecrement = false;

            try
            {
                if (!identity._safeTokenHandle.IsInvalid && identity._safeTokenHandle != SafeAccessTokenHandle.InvalidHandle && identity._safeTokenHandle.DangerousGetHandle() != IntPtr.Zero)
                {
                    identity._safeTokenHandle.DangerousAddRef(ref mustDecrement);

                    if (!identity._safeTokenHandle.IsInvalid && identity._safeTokenHandle.DangerousGetHandle() != IntPtr.Zero)
                        CreateFromToken(identity._safeTokenHandle.DangerousGetHandle());

                    _authType = identity._authType;
                    _isAuthenticated = identity._isAuthenticated;
                }
            }
            finally
            {
                if (mustDecrement)
                    identity._safeTokenHandle.DangerousRelease();
            }
        }

        private static string GetAuthType(WindowsIdentity identity)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }
            return identity._authType;
        }

        internal IntPtr GetTokenInternal()
        {
            return _safeTokenHandle.DangerousGetHandle();
        }


        internal WindowsIdentity(ClaimsIdentity claimsIdentity, IntPtr userToken)
            : base(claimsIdentity)
        {
            if (userToken != IntPtr.Zero && userToken.ToInt64() > 0)
            {
                CreateFromToken(userToken);
            }
        }

        /// <summary>
        /// Returns a new instance of the base, used when serializing the WindowsIdentity.
        /// </summary>
        internal ClaimsIdentity CloneAsBase()
        {
            return base.Clone();
        }

        /// <summary>
        /// Returns a new instance of <see cref="WindowsIdentity"/> with values copied from this object.
        /// </summary>
        public override ClaimsIdentity Clone()
        {
            return new WindowsIdentity(this);
        }

        /// <summary>
        /// Gets the 'User Claims' from the NTToken that represents this identity
        /// </summary>
        public virtual IEnumerable<Claim> UserClaims
        {
            get
            {
                InitializeClaims();

                return _userClaims.ToArray();
            }
        }

        /// <summary>
        /// Gets the 'Device Claims' from the NTToken that represents the device the identity is using
        /// </summary>
        public virtual IEnumerable<Claim> DeviceClaims
        {
            get
            {
                InitializeClaims();

                return _deviceClaims.ToArray();
            }
        }

        /// <summary>
        /// Gets the claims as <see cref="IEnumerable{Claim}"/>, associated with this <see cref="WindowsIdentity"/>.
        /// Includes UserClaims and DeviceClaims.
        /// </summary>
        public override IEnumerable<Claim> Claims
        {
            get
            {
                if (!_claimsInitialized)
                {
                    InitializeClaims();
                }

                foreach (Claim claim in base.Claims)
                    yield return claim;

                foreach (Claim claim in _userClaims)
                    yield return claim;

                foreach (Claim claim in _deviceClaims)
                    yield return claim;
            }
        }

        /// <summary>
        /// Intenal method to initialize the claim collection.
        /// Lazy init is used so claims are not initialzed until needed
        /// </summary>        
        private void InitializeClaims()
        {
            if (!_claimsInitialized)
            {
                lock (_claimsIntiailizedLock)
                {
                    if (!_claimsInitialized)
                    {
                        _userClaims = new List<Claim>();
                        _deviceClaims = new List<Claim>();

                        if (!String.IsNullOrEmpty(Name))
                        {
                            //
                            // Add the name claim only if the WindowsIdentity.Name is populated
                            // WindowsIdentity.Name will be null when it is the fake anonymous user
                            // with a token value of IntPtr.Zero
                            //
                            _userClaims.Add(new Claim(NameClaimType, Name, ClaimValueTypes.String, _issuerName, _issuerName, this));
                        }

                        // primary sid
                        AddPrimarySidClaim(_userClaims);

                        // group sids
                        AddGroupSidClaims(_userClaims);

                        _claimsInitialized = true;
                    }
                }
            }
        }

        /// <summary>
        /// Creates a collection of SID claims that represent the users groups.
        /// </summary>
        private void AddGroupSidClaims(List<Claim> instanceClaims)
        {
            // special case the anonymous identity.
            if (_safeTokenHandle.IsInvalid)
                return;

            SafeLocalAllocHandle safeAllocHandle = SafeLocalAllocHandle.InvalidHandle;
            SafeLocalAllocHandle safeAllocHandlePrimaryGroup = SafeLocalAllocHandle.InvalidHandle;
            try
            {
                // Retrieve the primary group sid
                safeAllocHandlePrimaryGroup = GetTokenInformation(_safeTokenHandle, TokenInformationClass.TokenPrimaryGroup);
                Interop.TOKEN_PRIMARY_GROUP primaryGroup = (Interop.TOKEN_PRIMARY_GROUP)Marshal.PtrToStructure<Interop.TOKEN_PRIMARY_GROUP>(safeAllocHandlePrimaryGroup.DangerousGetHandle());
                SecurityIdentifier primaryGroupSid = new SecurityIdentifier(primaryGroup.PrimaryGroup, true);

                // only add one primary group sid
                bool foundPrimaryGroupSid = false;

                // Retrieve all group sids, primary group sid is one of them
                safeAllocHandle = GetTokenInformation(_safeTokenHandle, TokenInformationClass.TokenGroups);
                int count = Marshal.ReadInt32(safeAllocHandle.DangerousGetHandle());
                IntPtr pSidAndAttributes = new IntPtr((long)safeAllocHandle.DangerousGetHandle() + (long)Marshal.OffsetOf<Interop.TOKEN_GROUPS>("Groups"));
                Claim claim;
                for (int i = 0; i < count; ++i)
                {
                    Interop.SID_AND_ATTRIBUTES group = (Interop.SID_AND_ATTRIBUTES)Marshal.PtrToStructure<Interop.SID_AND_ATTRIBUTES>(pSidAndAttributes);
                    uint mask = Interop.SecurityGroups.SE_GROUP_ENABLED | Interop.SecurityGroups.SE_GROUP_LOGON_ID | Interop.SecurityGroups.SE_GROUP_USE_FOR_DENY_ONLY;
                    SecurityIdentifier groupSid = new SecurityIdentifier(group.Sid, true);

                    if ((group.Attributes & mask) == Interop.SecurityGroups.SE_GROUP_ENABLED)
                    {
                        if (!foundPrimaryGroupSid && StringComparer.Ordinal.Equals(groupSid.Value, primaryGroupSid.Value))
                        {
                            claim = new Claim(ClaimTypes.PrimaryGroupSid, groupSid.Value, ClaimValueTypes.String, _issuerName, _issuerName, this);
                            claim.Properties.Add(ClaimTypes.WindowsSubAuthority, groupSid.IdentifierAuthority.ToString());
                            instanceClaims.Add(claim);
                            foundPrimaryGroupSid = true;
                        }
                        //Primary group sid generates both regular groupsid claim and primary groupsid claim
                        claim = new Claim(ClaimTypes.GroupSid, groupSid.Value, ClaimValueTypes.String, _issuerName, _issuerName, this);
                        claim.Properties.Add(ClaimTypes.WindowsSubAuthority, groupSid.IdentifierAuthority.ToString());
                        instanceClaims.Add(claim);
                    }
                    else if ((group.Attributes & mask) == Interop.SecurityGroups.SE_GROUP_USE_FOR_DENY_ONLY)
                    {
                        if (!foundPrimaryGroupSid && StringComparer.Ordinal.Equals(groupSid.Value, primaryGroupSid.Value))
                        {
                            claim = new Claim(ClaimTypes.DenyOnlyPrimaryGroupSid, groupSid.Value, ClaimValueTypes.String, _issuerName, _issuerName, this);
                            claim.Properties.Add(ClaimTypes.WindowsSubAuthority, groupSid.IdentifierAuthority.ToString());
                            instanceClaims.Add(claim);
                            foundPrimaryGroupSid = true;
                        }
                        //Primary group sid generates both regular groupsid claim and primary groupsid claim
                        claim = new Claim(ClaimTypes.DenyOnlySid, groupSid.Value, ClaimValueTypes.String, _issuerName, _issuerName, this);
                        claim.Properties.Add(ClaimTypes.WindowsSubAuthority, groupSid.IdentifierAuthority.ToString());
                        instanceClaims.Add(claim);
                    }
                    pSidAndAttributes = new IntPtr((long)pSidAndAttributes + Marshal.SizeOf<Interop.SID_AND_ATTRIBUTES>());
                }
            }
            finally
            {
                safeAllocHandle.Dispose();
                safeAllocHandlePrimaryGroup.Dispose();
            }
        }

        /// <summary>
        /// Creates a Windows SID Claim and adds to collection of claims.
        /// </summary>
        private void AddPrimarySidClaim(List<Claim> instanceClaims)
        {
            // special case the anonymous identity.
            if (_safeTokenHandle.IsInvalid)
                return;

            SafeLocalAllocHandle safeAllocHandle = SafeLocalAllocHandle.InvalidHandle;
            try
            {
                safeAllocHandle = GetTokenInformation(_safeTokenHandle, TokenInformationClass.TokenUser);
                Interop.SID_AND_ATTRIBUTES user = (Interop.SID_AND_ATTRIBUTES)Marshal.PtrToStructure<Interop.SID_AND_ATTRIBUTES>(safeAllocHandle.DangerousGetHandle());
                uint mask = Interop.SecurityGroups.SE_GROUP_USE_FOR_DENY_ONLY;

                SecurityIdentifier sid = new SecurityIdentifier(user.Sid, true);
                Claim claim;
                if (user.Attributes == 0)
                {
                    claim = new Claim(ClaimTypes.PrimarySid, sid.Value, ClaimValueTypes.String, _issuerName, _issuerName, this);
                    claim.Properties.Add(ClaimTypes.WindowsSubAuthority, sid.IdentifierAuthority.ToString());
                    instanceClaims.Add(claim);
                }
                else if ((user.Attributes & mask) == Interop.SecurityGroups.SE_GROUP_USE_FOR_DENY_ONLY)
                {
                    claim = new Claim(ClaimTypes.DenyOnlyPrimarySid, sid.Value, ClaimValueTypes.String, _issuerName, _issuerName, this);
                    claim.Properties.Add(ClaimTypes.WindowsSubAuthority, sid.IdentifierAuthority.ToString());
                    instanceClaims.Add(claim);
                }
            }
            finally
            {
                safeAllocHandle.Dispose();
            }
        }
    }

    internal enum WinSecurityContext
    {
        Thread = 1, // OpenAsSelf = false
        Process = 2, // OpenAsSelf = true
        Both = 3 // OpenAsSelf = true, then OpenAsSelf = false
    }

    internal enum ImpersonationQueryResult
    {
        Impersonated = 0,    // current thread is impersonated
        NotImpersonated = 1,    // current thread is not impersonated
        Failed = 2     // failed to query 
    }

    internal enum TokenType : int
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    internal enum TokenInformationClass : int
    {
        TokenUser = 1,
        TokenGroups,
        TokenPrivileges,
        TokenOwner,
        TokenPrimaryGroup,
        TokenDefaultDacl,
        TokenSource,
        TokenType,
        TokenImpersonationLevel,
        TokenStatistics,
        TokenRestrictedSids,
        TokenSessionId,
        TokenGroupsAndPrivileges,
        TokenSessionReference,
        TokenSandBoxInert,
        TokenAuditPolicy,
        TokenOrigin,
        TokenElevationType,
        TokenLinkedToken,
        TokenElevation,
        TokenHasRestrictions,
        TokenAccessInformation,
        TokenVirtualizationAllowed,
        TokenVirtualizationEnabled,
        TokenIntegrityLevel,
        TokenUIAccess,
        TokenMandatoryPolicy,
        TokenLogonSid,
        TokenIsAppContainer,
        TokenCapabilities,
        TokenAppContainerSid,
        TokenAppContainerNumber,
        TokenUserClaimAttributes,
        TokenDeviceClaimAttributes,
        TokenRestrictedUserClaimAttributes,
        TokenRestrictedDeviceClaimAttributes,
        TokenDeviceGroups,
        TokenRestrictedDeviceGroups,
        MaxTokenInfoClass  // MaxTokenInfoClass should always be the last enum
    }
}