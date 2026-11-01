use std::fmt::{Debug, Display, Formatter};
use std::ops::{Deref, DerefMut};

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use sqlx::database::HasArguments;
use sqlx::encode::IsNull;
use sqlx::{Database, Encode, Type};

#[repr(transparent)]
pub struct Redacted<T>(T);

impl<T> Redacted<T> {
    pub fn new(t: T) -> Self {
        Self(t)
    }

    pub fn into_inner(self) -> T {
        self.0
    }

    pub fn as_inner(&self) -> &T {
        &self.0
    }
}

impl<T> Display for Redacted<T> {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        write!(f, "[redacted]")
    }
}

impl<T> Debug for Redacted<T> {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        write!(f, "[redacted]")
    }
}

impl<T> Deref for Redacted<T> {
    type Target = T;

    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

impl<T> DerefMut for Redacted<T> {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.0
    }
}

impl<T: Clone> Clone for Redacted<T> {
    fn clone(&self) -> Self {
        Redacted(self.0.clone())
    }
}

impl<T: Copy> Copy for Redacted<T> {}

impl<T> From<T> for Redacted<T> {
    fn from(t: T) -> Self {
        Self(t)
    }
}

impl<T: Serialize> Serialize for Redacted<T> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        self.0.serialize(serializer)
    }
}

impl<'de, T: Deserialize<'de>> Deserialize<'de> for Redacted<T> {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        T::deserialize(deserializer).map(Redacted)
    }
}

impl<T: serde_bytes::Serialize> serde_bytes::Serialize for Redacted<T> {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serde_bytes::Serialize::serialize(&self.0, serializer)
    }
}

impl<'de, T: serde_bytes::Deserialize<'de>> serde_bytes::Deserialize<'de> for Redacted<T> {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        serde_bytes::Deserialize::deserialize(deserializer).map(Redacted)
    }
}

impl<'q, DB: Database, T: Encode<'q, DB>> Encode<'q, DB> for Redacted<T> {
    fn encode(self, buf: &mut <DB as HasArguments<'q>>::ArgumentBuffer) -> IsNull
    where
        Self: Sized,
    {
        self.0.encode(buf)
    }

    fn encode_by_ref(&self, buf: &mut <DB as HasArguments<'q>>::ArgumentBuffer) -> IsNull {
        self.0.encode_by_ref(buf)
    }

    fn produces(&self) -> Option<DB::TypeInfo> {
        self.0.produces()
    }

    fn size_hint(&self) -> usize {
        self.0.size_hint()
    }
}

impl<DB: Database, T: Type<DB>> Type<DB> for Redacted<T> {
    fn type_info() -> DB::TypeInfo {
        T::type_info()
    }

    fn compatible(ty: &DB::TypeInfo) -> bool {
        T::compatible(ty)
    }
}
