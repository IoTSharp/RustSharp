#![no_std]
#[derive(Debug)]
pub mod model {
    pub struct Pair<T: Copy> {
        pub first: T,
        second: i32,
    }

    pub enum Choice<T> {
        One(T),
        None,
    }

    pub type Count = usize;
    pub const LIMIT: usize = 4;

    pub fn identity<T: Copy>(value: T) -> T {
        return value;
    }
}

use crate::model::Pair as PublicPair;
