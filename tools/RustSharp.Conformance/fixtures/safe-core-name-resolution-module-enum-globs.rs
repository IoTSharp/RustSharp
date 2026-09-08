mod api { pub fn f() {} fn hidden() {} }
enum Choice { A, B }
use crate::api::*;
use crate::Choice::*;
fn main() { f(); A; B; }
