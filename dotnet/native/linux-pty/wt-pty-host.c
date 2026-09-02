#define _GNU_SOURCE

#include <errno.h>
#include <poll.h>
#include <pty.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

static bool write_all(int fd, const void *buffer, size_t length) {
    const uint8_t *bytes = buffer;
    while (length > 0) {
        ssize_t written = write(fd, bytes, length);
        if (written < 0) {
            if (errno == EINTR) continue;
            return false;
        }

        bytes += written;
        length -= (size_t)written;
    }

    return true;
}

static bool read_all(int fd, void *buffer, size_t length) {
    uint8_t *bytes = buffer;
    while (length > 0) {
        ssize_t count = read(fd, bytes, length);
        if (count == 0) return false;
        if (count < 0) {
            if (errno == EINTR) continue;
            return false;
        }

        bytes += count;
        length -= (size_t)count;
    }

    return true;
}

static bool read_header(char *buffer, size_t capacity) {
    size_t length = 0;
    while (length + 1 < capacity) {
        char value;
        ssize_t count = read(STDIN_FILENO, &value, 1);
        if (count == 0) return false;
        if (count < 0) {
            if (errno == EINTR) continue;
            return false;
        }

        if (value == '\n') {
            buffer[length] = '\0';
            return true;
        }

        buffer[length++] = value;
    }

    errno = EMSGSIZE;
    return false;
}

static void resize_pty(int master, pid_t child, unsigned columns, unsigned rows) {
    struct winsize size = {
        .ws_row = (unsigned short)rows,
        .ws_col = (unsigned short)columns,
    };
    if (ioctl(master, TIOCSWINSZ, &size) == 0) {
        kill(-child, SIGWINCH);
    }
}

int main(int argc, char **argv) {
    if (argc != 5) {
        fprintf(stderr, "usage: wt-pty-host <columns> <rows> <cwd> <command>\n");
        return 64;
    }

    unsigned columns = (unsigned)strtoul(argv[1], NULL, 10);
    unsigned rows = (unsigned)strtoul(argv[2], NULL, 10);
    if (columns == 0 || columns > UINT16_MAX || rows == 0 || rows > UINT16_MAX) {
        fprintf(stderr, "invalid terminal dimensions\n");
        return 64;
    }

    struct winsize size = {
        .ws_row = (unsigned short)rows,
        .ws_col = (unsigned short)columns,
    };
    int master = -1;
    pid_t child = forkpty(&master, NULL, NULL, &size);
    if (child < 0) {
        perror("forkpty");
        return 71;
    }

    if (child == 0) {
        setenv("TERM", getenv("TERM") ? getenv("TERM") : "xterm-256color", 0);
        if (argv[3][0] != '\0' && chdir(argv[3]) != 0) {
            perror("chdir");
            _exit(72);
        }

        execl("/bin/sh", "sh", "-lc", argv[4], (char *)NULL);
        perror("exec");
        _exit(127);
    }

    bool input_open = true;
    bool master_open = true;
    uint8_t buffer[16384];
    while (master_open) {
        struct pollfd descriptors[2] = {
            { .fd = master, .events = POLLIN },
            { .fd = input_open ? STDIN_FILENO : -1, .events = POLLIN },
        };
        int result = poll(descriptors, 2, -1);
        if (result < 0) {
            if (errno == EINTR) continue;
            perror("poll");
            break;
        }

        if (descriptors[0].revents & (POLLIN | POLLHUP)) {
            ssize_t count = read(master, buffer, sizeof(buffer));
            if (count > 0) {
                if (!write_all(STDOUT_FILENO, buffer, (size_t)count)) break;
            } else if (count == 0 || errno == EIO) {
                master_open = false;
            } else if (errno != EINTR) {
                perror("read pty");
                break;
            }
        }

        if (input_open && descriptors[1].revents & (POLLIN | POLLHUP)) {
            char header[96];
            if (!read_header(header, sizeof(header))) {
                input_open = false;
                kill(-child, SIGHUP);
                continue;
            }

            if (header[0] == 'D' && header[1] == ' ') {
                size_t length = (size_t)strtoull(header + 2, NULL, 10);
                while (length > 0) {
                    size_t chunk = length < sizeof(buffer) ? length : sizeof(buffer);
                    if (!read_all(STDIN_FILENO, buffer, chunk) ||
                        !write_all(master, buffer, chunk)) {
                        input_open = false;
                        kill(-child, SIGHUP);
                        break;
                    }

                    length -= chunk;
                }
            } else if (header[0] == 'R' && header[1] == ' ') {
                unsigned new_columns = 0;
                unsigned new_rows = 0;
                if (sscanf(header + 2, "%u %u", &new_columns, &new_rows) == 2 &&
                    new_columns > 0 && new_columns <= UINT16_MAX &&
                    new_rows > 0 && new_rows <= UINT16_MAX) {
                    resize_pty(master, child, new_columns, new_rows);
                }
            } else if (strcmp(header, "C") == 0) {
                input_open = false;
                kill(-child, SIGHUP);
            }
        }
    }

    close(master);
    int status = 0;
    while (waitpid(child, &status, 0) < 0 && errno == EINTR) {
    }

    if (WIFEXITED(status)) return WEXITSTATUS(status);
    if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
    return 1;
}
