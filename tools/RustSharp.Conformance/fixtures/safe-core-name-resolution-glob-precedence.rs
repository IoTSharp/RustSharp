mod api { pub fn f() {} pub fn Name() {} pub const A: i32 = 1; }
use crate::api::*;
fn f() {}
type Name = i32;
const A: i32 = 2;
fn main() { f(); Name(); A; }
