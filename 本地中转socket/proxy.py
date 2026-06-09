import socket
import threading

# ========== 配置 ==========
LOCAL_HOST = "127.0.0.1"
LOCAL_PORT = 9000
REMOTE_HOST = "212.129.231.246"
REMOTE_PORT = 8117
# ==========================

def forward(src, dst, name):
    try:
        while True:
            data = src.recv(4096)
            if not data:
                break
            dst.sendall(data)
    except Exception as e:
        print(f"[{name}] Closed: {e}")
    finally:
        src.close()
        dst.close()

def handle(client_sock, client_addr):
    print(f"[PROXY] {client_addr} -> {REMOTE_HOST}:{REMOTE_PORT}")
    try:
        remote_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        remote_sock.connect((REMOTE_HOST, REMOTE_PORT))

        t1 = threading.Thread(target=forward, args=(client_sock, remote_sock, "C->S"), daemon=True)
        t2 = threading.Thread(target=forward, args=(remote_sock, client_sock, "S->C"), daemon=True)
        t1.start()
        t2.start()
        t1.join()
        t2.join()
    except Exception as e:
        print(f"[PROXY] Remote connect failed: {e}")
        client_sock.close()

def main():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((LOCAL_HOST, LOCAL_PORT))
    server.listen(5)
    print(f"[PROXY] Listening on {LOCAL_HOST}:{LOCAL_PORT}")
    print(f"[PROXY] Forwarding to {REMOTE_HOST}:{REMOTE_PORT}")

    try:
        while True:
            client_sock, client_addr = server.accept()
            threading.Thread(target=handle, args=(client_sock, client_addr), daemon=True).start()
    except KeyboardInterrupt:
        print("\n[PROXY] Shut down.")
        server.close()

if __name__ == "__main__":
    main()
