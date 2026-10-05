// frozen P1 fixture: borrow-deref-projection
fn main() {
    let mut value = 1;
    let parent = &mut value;
    {
        let child = &mut *parent;
        *child = 7;
    }
    println!("{}", *parent);
}
