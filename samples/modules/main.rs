//! A small program built from documented source modules.

mod arithmetic;

use arithmetic::{self as math, *};

fn main() {
    println!("{}", answer());
    println!("{}", math::positive());
}
