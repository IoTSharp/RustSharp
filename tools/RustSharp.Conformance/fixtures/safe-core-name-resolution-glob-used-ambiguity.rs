mod a { pub fn f() {} }
mod b { pub fn f() {} }
use crate::a::*;
use crate::b::*;
fn main() { f(); }
