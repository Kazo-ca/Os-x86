; Os-x86 DOS test program (milestone 0, work item 11): switches to a graphical mode, what the
; guest test of `x86 run` expects to stop (decisions 164 A and 217 A). Assembled by
; images/freedos/build-freedos.py with NASM in the osx86-nasm container into
; images/build/freedos/programs/OSX86GFX.COM (never committed).
;
; Writes GFX, sets the VGA mode 13h (320x200, 256 colours), draws a diagonal, waits for a key,
; goes back to the text mode 3 and ends with ERRORLEVEL 0.
        org 100h

start:
        mov si, message
.print:
        lodsb
        test al, al
        jz .graphics
        mov ah, 0Eh
        mov bx, 0007h
        int 10h
        jmp .print

.graphics:
        mov ax, 0013h
        int 10h
        mov ax, 0A000h
        mov es, ax
        xor di, di
        mov cx, 200
        mov al, 0Eh
.line:
        mov [es:di], al
        add di, 321
        loop .line

        xor ah, ah
        int 16h
        mov ax, 0003h
        int 10h
        mov ax, 4C00h
        int 21h

message db "GFX: switching to mode 13h", 0Dh, 0Ah, 0
