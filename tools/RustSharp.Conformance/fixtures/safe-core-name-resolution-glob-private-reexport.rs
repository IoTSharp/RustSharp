mod outer {
    mod source { pub(super) fn f() {} }
    pub use self::source::*;
}
fn main() { outer::f(); }
