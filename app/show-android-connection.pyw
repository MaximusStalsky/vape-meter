"""Show the Android device key from the current user's Windows vault."""
import json
import os
from pathlib import Path
import re
import subprocess
import tkinter as tk
from tkinter import messagebox

import qrcode
from PIL import ImageTk


def read_android_key():
    vault = Path(os.environ["LOCALAPPDATA"]) / "VapeMeter" / "device-tokens.dpapi"
    if not vault.is_file():
        raise RuntimeError("Локальное хранилище ключей Vape Meter не найдено.")

    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
         "$ErrorActionPreference='Stop'; "
         "$encrypted=[Console]::In.ReadToEnd(); "
         "[Net.NetworkCredential]::new('', (ConvertTo-SecureString $encrypted)).Password"],
        input=vault.read_text(encoding="utf-8").strip(),
        text=True, capture_output=True, timeout=12, check=True,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    key = json.loads(result.stdout).get("android")
    if not isinstance(key, str) or not re.fullmatch(r"android\.[A-Za-z0-9_-]{43}", key):
        raise RuntimeError("В этом хранилище нет подходящего ключа Android.")
    return key


def show(root, key):
    root.title("Vape Meter · Подключение Android")
    root.resizable(False, False)
    root.configure(bg="#171022")
    root.option_add("*Font", "{Segoe UI} 10")

    holder = tk.Frame(root, bg="#171022", padx=24, pady=20)
    holder.pack()
    tk.Label(holder, text="Подключение Android", font=("Segoe UI", 17, "bold"),
             bg="#171022", fg="#f5efff").pack()
    tk.Label(holder, text="Отсканируй QR на телефоне и скопируй текст.\n"
             "Вставь ключ при первом запуске Vape Meter.",
             bg="#171022", fg="#d7c8ea", justify="center").pack(pady=(8, 14))

    qr = qrcode.QRCode(box_size=7, border=4, error_correction=qrcode.constants.ERROR_CORRECT_M)
    qr.add_data(key)
    qr.make(fit=True)
    image = ImageTk.PhotoImage(qr.make_image(fill_color="#171022", back_color="white").convert("RGB"))
    picture = tk.Label(holder, image=image, bd=0, bg="white")
    picture.image = image
    picture.pack()

    copied = tk.StringVar()
    tk.Label(holder, textvariable=copied, bg="#171022", fg="#90f6d4").pack(pady=(8, 0))

    def copy_key():
        root.clipboard_clear()
        root.clipboard_append(key)
        copied.set("Ключ скопирован")

    def close():
        try:
            if copied.get() and root.clipboard_get() == key:
                root.clipboard_clear()
        except tk.TclError:
            pass
        root.destroy()

    tk.Button(holder, text="Скопировать ключ", command=copy_key, bg="#7936b8", fg="white",
              activebackground="#9a4cdb", activeforeground="white", relief="flat",
              padx=18, pady=7).pack(pady=(7, 0))
    root.protocol("WM_DELETE_WINDOW", close)
    root.bind("<Escape>", lambda _event: close())
    root.deiconify()


def main():
    root = tk.Tk()
    root.withdraw()
    try:
        show(root, read_android_key())
    except (OSError, ValueError, subprocess.SubprocessError, RuntimeError):
        messagebox.showerror("Vape Meter", "Ключ Android недоступен. Открой это окно "
                             "под учётной записью Windows, где настроен Vape Meter.", parent=root)
        root.destroy()
        return
    root.mainloop()


if __name__ == "__main__":
    main()
