mod guarded {
    #![allow(dead_code)]
    pub fn f() {}
}
fn local() {}
fn main() { local(); }
