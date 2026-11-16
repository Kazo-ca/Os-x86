; Os-x86 DOS test program (milestone 0, work item 11): what the guest test of `x86 run` drives.
; Assembled by images/freedos/build-freedos.py with NASM in the osx86-nasm container into
; images/build/freedos/programs/OSX86KEY.COM (never committed).
;
; Writes a title in yellow on blue (attribute 1Eh, the colours of the screen), its command tail,
; then READY, and reads keys with the BIOS (int 16h): for each one it writes K=ssaa (scancode and
; ASCII in hexadecimal). Enter ends it with ERRORLEVEL 42; Ctrl+C (ASCII 03h) ends it with 3, so
; the test sees that Ctrl+C reaches the program (decision 216 A). It writes with the BIOS only and
; points int 23h at an IRET, so that DOS never takes Ctrl+C itself.
        org 100h

start:
        mov ax, 2523h               ; int 23h (Ctrl+C of DOS) -> ignore_break
        mov dx, ignore_break
        int 21h

        mov si, title
        mov bl, 1Eh
        call print_attr
        call newline

        mov si, args_label
        call print
        mov cl, [80h]               ; the command tail of the PSP: its length, then the text
        xor ch, ch
        mov si, 81h
.args:
        jcxz .args_done
        lodsb
        call putc
        dec cx
        jmp .args
.args_done:
        mov al, ']'
        call putc
        call newline

        mov si, ready
        call print
        call newline

.loop:
        xor ah, ah
        int 16h                     ; AH = scancode, AL = ASCII
        cmp al, 03h
        je .ctrl_c
        cmp al, 0Dh
        je .enter
        push ax
        mov si, key_label
        call print
        pop ax
        push ax
        mov al, ah
        call print_hex
        pop ax
        call print_hex
        mov al, ' '
        call putc
        jmp .loop

.ctrl_c:
        mov si, ctrl_c_text
        call print
        call newline
        mov ax, 4C03h
        int 21h

.enter:
        mov si, end_text
        call print
        call newline
        mov ax, 4C2Ah
        int 21h

ignore_break:
        iret

; putc: writes AL with the BIOS teletype (page 0), the cursor advances.
putc:
        push ax
        push bx
        mov ah, 0Eh
        mov bx, 0007h
        int 10h
        pop bx
        pop ax
        ret

newline:
        mov al, 0Dh
        call putc
        mov al, 0Ah
        call putc
        ret

; print: writes the zero-terminated string at SI.
print:
        lodsb
        test al, al
        jz .done
        call putc
        jmp print
.done:
        ret

; print_attr: writes the zero-terminated string at SI with the attribute BL.
print_attr:
        lodsb
        test al, al
        jz .done
        push bx
        push cx
        mov ah, 09h                 ; character and attribute at the cursor
        mov bh, 0
        mov cx, 1
        int 10h
        pop cx
        pop bx
        call putc                   ; the same character again, to advance the cursor
        jmp print_attr
.done:
        ret

; print_hex: writes AL as two hexadecimal digits.
print_hex:
        push ax
        push ax
        shr al, 4
        call hex_digit
        pop ax
        and al, 0Fh
        call hex_digit
        pop ax
        ret

hex_digit:
        add al, '0'
        cmp al, '9'
        jbe .write
        add al, 'A' - '9' - 1
.write:
        call putc
        ret

title       db "OSX86 DOS TEST", 0
args_label  db "ARGS:[", 0
ready       db "READY: type keys, Enter ends (42), Ctrl+C ends (3)", 0
key_label   db "K=", 0
ctrl_c_text db " CTRL-C", 0
end_text    db " END", 0
