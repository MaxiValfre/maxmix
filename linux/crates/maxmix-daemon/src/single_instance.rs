//! Single-instance enforcement via an exclusive advisory lock on a file in the
//! XDG runtime dir. The lock is held for the process lifetime and released when
//! the returned guard drops (or the process exits).

use std::fs::{File, OpenOptions};
use std::path::PathBuf;

use fs2::FileExt;

/// Held for the process lifetime; dropping it releases the lock.
pub struct InstanceLock {
    _file: File,
}

/// Where the lock file lives: `$XDG_RUNTIME_DIR/maxmix.lock`, falling back to a
/// temp dir.
#[must_use]
pub fn lock_path() -> PathBuf {
    dirs::runtime_dir()
        .unwrap_or_else(std::env::temp_dir)
        .join("maxmix.lock")
}

/// Try to become the single running instance. Returns `None` if another
/// instance already holds the lock.
#[must_use]
pub fn acquire() -> Option<InstanceLock> {
    let path = lock_path();
    let file = OpenOptions::new()
        .create(true)
        .read(true)
        .write(true)
        .open(&path)
        .ok()?;
    match file.try_lock_exclusive() {
        Ok(()) => Some(InstanceLock { _file: file }),
        Err(_) => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn second_acquire_on_same_file_fails() {
        // Lock a temp file directly to avoid clobbering a real daemon's lock.
        let path = std::env::temp_dir().join(format!("maxmix-test-{}.lock", std::process::id()));
        let f1 = OpenOptions::new().create(true).read(true).write(true).open(&path).unwrap();
        assert!(f1.try_lock_exclusive().is_ok());
        let f2 = OpenOptions::new().create(true).read(true).write(true).open(&path).unwrap();
        assert!(f2.try_lock_exclusive().is_err(), "second lock must fail");
        let _ = fs2::FileExt::unlock(&f1);
        let _ = std::fs::remove_file(&path);
    }
}
