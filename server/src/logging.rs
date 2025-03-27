use anyhow::{Context, Result};
use lazy_static::lazy_static;
use log::{Level, LevelFilter};
use parking_lot::RwLock;
use regex::Regex;

lazy_static! {
    pub static ref LOG_LEVEL: RwLock<Level> = RwLock::new(Level::Info);
    static ref KEY_REGEX: Regex = Regex::new(r#"extrachat_[1-9A-HJ-NP-Za-km-z]+_[1-9A-HJ-NP-Za-km-z]+"#).unwrap();
}

pub fn setup() -> Result<()> {
    fern::Dispatch::new()
        .filter(|metadata| {
            match metadata.target() {
                "extra_chat_server" | "sqlx" => true,
                x if x.starts_with("extra_chat_server::") => true,
                x if x.starts_with("sqlx::") => true,
                _ => false,
            }
        })
        .format(|out, message, record| {
            let message = format!("{}", message);
            let message = KEY_REGEX.replace_all(&message, "[redacted]");

            out.finish(format_args!(
                "[{}][{}][{}:{}] {}",
                chrono::Local::now().format("%Y-%m-%d %H:%M:%S %Z"),
                record.level(),
                record.file().unwrap_or("?"),
                record.line().unwrap_or(0),
                message,
            ))
        })
        .chain(fern::Dispatch::new()
            .filter(|meta| {
                meta.level() <= *LOG_LEVEL.read()
            })
            .chain(std::io::stdout())
        )
        .chain(fern::Dispatch::new()
            .filter(|meta| {
                meta.level() <= *LOG_LEVEL.read()
            })
            .chain(fern::log_file("extrachat.log")?)
        )
        .apply()
        .context("could not set up logging facility")
}
