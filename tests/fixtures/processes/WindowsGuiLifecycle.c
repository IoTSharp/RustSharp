/* Windows GUI / no CRT / no CLR; only kernel32. No console/shell/breakaway.
 * /GS- /Zl /W4 /WX; /NODEFAULTLIB /ENTRY:TinyEntry /SUBSYSTEM:WINDOWS.
 * Exact Job membership/identity is independently proved by the controller.
 */
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
static BOOL Eq(const WCHAR* left,const WCHAR* right)
{
    DWORD index; ULONGLONG start=GetTickCount64();
    for(index=0;index<1024 && GetTickCount64()-start<1000;index++) {
        if(left[index]!=right[index]) return FALSE;
        if(left[index]==0) return TRUE;
    }
    return FALSE;
}
static void Zero(void* memory,SIZE_T bytes)
{
    SIZE_T index;volatile BYTE* target=(volatile BYTE*)memory;ULONGLONG start=GetTickCount64();
    if(bytes>4096) ExitProcess(81);
    for(index=0;index<bytes && index<4096 && GetTickCount64()-start<1000;index++) target[index]=0;
    if(index!=bytes) ExitProcess(81);
}
static BOOL Append(WCHAR* target,DWORD* length,const WCHAR* value)
{
    DWORD index;ULONGLONG start=GetTickCount64();
    for(index=0;index<1024 && GetTickCount64()-start<1000;index++) {
        if(value[index]==0) return TRUE;
        if(*length>=4095) return FALSE;
        target[(*length)++]=value[index];target[*length]=0;
    }
    return FALSE;
}
static BOOL WriteBytes(HANDLE stream,const BYTE* bytes,DWORD count)
{
    DWORD written=0;
    return stream && stream!=INVALID_HANDLE_VALUE && GetFileType(stream)==FILE_TYPE_PIPE && WriteFile(stream,bytes,count,&written,NULL) && written==count;
}
static DWORD Parse(WCHAR values[4][1024])
{
    const WCHAR* command=GetCommandLineW();DWORD argc=0,pos=0,index;BOOL quoted;ULONGLONG start=GetTickCount64();
    for(index=0;index<4 && pos<4096 && GetTickCount64()-start<1000;index++) {
        DWORD length=0;
        for(;pos<4096 && command[pos]==L' ' && GetTickCount64()-start<1000;pos++) {}
        if(pos>=4096 || GetTickCount64()-start>=1000) return 0;
        if(command[pos]==0) return argc;
        quoted=command[pos]==L'"';if(quoted) pos++;
        for(;pos<4096 && length<1023 && GetTickCount64()-start<1000;pos++) {
            WCHAR ch=command[pos];
            if((quoted && ch==L'"') || (!quoted && (ch==L' ' || ch==0))) break;
            if(ch==0 || ch==L'"') return 0;
            values[argc][length++]=ch;
        }
        if(pos>=4096 || length>=1023 || GetTickCount64()-start>=1000) return 0;
        values[argc][length]=0;argc++;
        if(quoted){if(command[pos]!=L'"') return 0;pos++;}
        if(command[pos]!=0 && command[pos]!=L' ') return 0;
    }
    for(index=0;index<4096 && pos<4096 && command[pos]==L' ' && GetTickCount64()-start<1000;index++,pos++) {}
    return pos<4096 && command[pos]==0 && GetTickCount64()-start<1000?argc:0;
}
static BOOL Ready(HANDLE reader)
{
    DWORD check,available=0,read=0;BYTE bytes[6];ULONGLONG start=GetTickCount64();
    for(check=0;check<100 && GetTickCount64()-start<2000;check++) {
        if(!PeekNamedPipe(reader,NULL,0,NULL,&available,NULL)) return FALSE;
        if(available>=6) {
            /* Sole reader; exactly six already available bytes, not an unbounded read. */
            if(!ReadFile(reader,bytes,6,&read,NULL) || read!=6 || GetTickCount64()-start>=2000) return FALSE;
            return bytes[0]=='R' && bytes[1]=='E' && bytes[2]=='A' && bytes[3]=='D' && bytes[4]=='Y' && bytes[5]=='\n';
        }
        Sleep(20);
    }
    return FALSE;
}
static BOOL StartChild(const WCHAR* role,const WCHAR* id)
{
    /* Static fixed buffers avoid compiler __chkstk/CRT helpers. One spawn per role/process. */
    static WCHAR image[1024],command[4096];DWORD imageLength,length=0;BOOL ok=FALSE,initialized=FALSE,created=FALSE;
    HANDLE reader=NULL,writer=NULL;HANDLE handles[3];SECURITY_ATTRIBUTES security;
    STARTUPINFOEXW startup;PROCESS_INFORMATION process;SIZE_T size=0;void* attributes=NULL;HANDLE heap=GetProcessHeap();BOOL inJob=FALSE;
    ULONGLONG start=GetTickCount64();DWORD cleanupErrors=0;
    Zero(&startup,sizeof(startup));Zero(&process,sizeof(process));command[0]=0;
    imageLength=GetModuleFileNameW(NULL,image,1024);
    if(imageLength==0 || imageLength>=1024) goto done;
    if(!Append(command,&length,L"\"") || !Append(command,&length,image) || !Append(command,&length,L"\" \"--fixture\" \"") || !Append(command,&length,role) || !Append(command,&length,L"\" \"") || !Append(command,&length,id) || !Append(command,&length,L"\"")) goto done;
    security.nLength=sizeof(security);security.lpSecurityDescriptor=NULL;security.bInheritHandle=TRUE;
    if(!CreatePipe(&reader,&writer,&security,0) || !SetHandleInformation(reader,HANDLE_FLAG_INHERIT,0)) goto done;
    handles[0]=GetStdHandle(STD_INPUT_HANDLE);handles[1]=writer;handles[2]=GetStdHandle(STD_ERROR_HANDLE);
    if(handles[0]==NULL || handles[0]==INVALID_HANDLE_VALUE || handles[2]==NULL || handles[2]==INVALID_HANDLE_VALUE) goto done;
    if(InitializeProcThreadAttributeList(NULL,1,0,&size) || GetLastError()!=ERROR_INSUFFICIENT_BUFFER || size==0 || size>4096) goto done;
    attributes=HeapAlloc(heap,0,size);if(!attributes) goto done;
    if(!InitializeProcThreadAttributeList(attributes,1,0,&size)) goto done;initialized=TRUE;
    if(!UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,handles,sizeof(handles),NULL,NULL)) goto done;
    startup.StartupInfo.cb=sizeof(startup);startup.StartupInfo.dwFlags=STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput=handles[0];startup.StartupInfo.hStdOutput=writer;startup.StartupInfo.hStdError=handles[2];startup.lpAttributeList=attributes;
    if(GetTickCount64()-start>=2000) goto done;
    if(!CreateProcessW(image,command,NULL,NULL,TRUE,EXTENDED_STARTUPINFO_PRESENT|CREATE_SUSPENDED,NULL,NULL,&startup.StartupInfo,&process)) goto done;
    created=TRUE;
    if(!IsProcessInJob(process.hProcess,NULL,&inJob) || !inJob || ResumeThread(process.hThread)!=1) goto done;
    if(!CloseHandle(writer)){cleanupErrors++;goto done;}writer=NULL;
    if(!Ready(reader) || GetTickCount64()-start>=2500) goto done;
    ok=TRUE;
done:
    /* Exact controller Job owns all child lifetimes; never kill a PID here. */
    if(created){if(!CloseHandle(process.hThread)) cleanupErrors++;if(!CloseHandle(process.hProcess)) cleanupErrors++;}
    if(initialized) DeleteProcThreadAttributeList(attributes);
    if(attributes && !HeapFree(heap,0,attributes)) cleanupErrors++;
    if(writer && !CloseHandle(writer)) cleanupErrors++;
    if(reader && !CloseHandle(reader)) cleanupErrors++;
    return ok && cleanupErrors==0;
}
static void BoundedHold(void)
{
    DWORD index;ULONGLONG start=GetTickCount64();
    for(index=0;index<600 && GetTickCount64()-start<12000;index++) Sleep(20);
}
__declspec(noreturn) void WINAPI TinyEntry(void)
{
    static WCHAR values[4][1024];DWORD argc=Parse(values);BOOL member=FALSE;
    static const BYTE tiny[11]={'t','i','n','y',' ','r','o','o','t','\r','\n'};
    static const BYTE ready[6]={'R','E','A','D','Y','\n'};
    static const BYTE natural[17]={'c','o','n','t','r','o','l','l','e','d',' ','r','o','o','t','\r','\n'};
    static const BYTE error[19]={'c','o','n','t','r','o','l','l','e','d',' ','e','x','i','t',' ','1','7','\n'};
    if(!IsProcessInJob(GetCurrentProcess(),NULL,&member) || !member) ExitProcess(82);
    if(argc==1){if(!WriteBytes(GetStdHandle(STD_OUTPUT_HANDLE),tiny,11)) ExitProcess(73);ExitProcess(0);}
    if(argc!=4 || !Eq(values[1],L"--fixture")) ExitProcess(83);
    if(!Eq(values[3],L"natural-exit") && !Eq(values[3],L"exception-exit") && !Eq(values[3],L"timeout") && !Eq(values[3],L"cancellation") && !Eq(values[3],L"descendants-survive-root") && !Eq(values[3],L"nested-grandchild") && !Eq(values[3],L"assignment-or-capability-failure") && !Eq(values[3],L"identity-reuse-rejection")) ExitProcess(84);
    if(Eq(values[2],L"root")) {
        if(Eq(values[3],L"exception-exit")){if(!WriteBytes(GetStdHandle(STD_ERROR_HANDLE),error,19)) ExitProcess(73);ExitProcess(17);}
        if(Eq(values[3],L"timeout") || Eq(values[3],L"cancellation")){BoundedHold();ExitProcess(0);}
        if(Eq(values[3],L"descendants-survive-root") || Eq(values[3],L"nested-grandchild")){if(!StartChild(L"child",values[3])) ExitProcess(85);ExitProcess(0);}
        if(!WriteBytes(GetStdHandle(STD_OUTPUT_HANDLE),natural,17)) ExitProcess(73);ExitProcess(0);
    }
    if(Eq(values[2],L"child") && (Eq(values[3],L"descendants-survive-root") || Eq(values[3],L"nested-grandchild"))) {
        if(Eq(values[3],L"nested-grandchild") && !StartChild(L"grandchild",values[3])) ExitProcess(85);
        if(!WriteBytes(GetStdHandle(STD_OUTPUT_HANDLE),ready,6)) ExitProcess(73);BoundedHold();ExitProcess(0);
    }
    if(Eq(values[2],L"grandchild") && Eq(values[3],L"nested-grandchild")){if(!WriteBytes(GetStdHandle(STD_OUTPUT_HANDLE),ready,6)) ExitProcess(73);BoundedHold();ExitProcess(0);}
    ExitProcess(86);
}
