// frozen P1 fixture: borrow-index-projection
fn main() {
    let mut values = [1, 2];
    {
        let left = &mut values[0];
        *left = 3;
        println!("{}", *left);
    }
    {
        let right = &mut values[1];
        *right = 4;
        println!("{}", *right);
    }
}
