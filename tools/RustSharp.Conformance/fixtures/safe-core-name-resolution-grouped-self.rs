mod api {
    pub fn f() {}
    pub mod nested { pub const VALUE: i32 = 7; }
}
use crate::api::{self as a, f as run, nested::{VALUE as number}};
use crate::api::{};
fn main() { run(); a::f(); number; }
